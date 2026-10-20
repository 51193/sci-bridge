#!/usr/bin/env python3
"""
Full Windows-to-Windows check: the shipped SciBridge.exe talking to the shipped
SciBridgeTestServer.exe, both running as Windows binaries under Wine.

This is the closest thing to the user's real flow that can be automated:
  * the test server binds a real port and serves real files
  * SciBridge.exe runs its own end-to-end self-test (connect, host-key prompt,
    start the loopback bridge, exercise every read-only operation)
  * a second run proves the host key was pinned, so no prompt appears again

Run:  <venv-python> tests/wine_selftest.py
"""
import json
import os
import pathlib
import shutil
import socket
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parent.parent
# Defaults point at the build outputs; pass --app/--server to verify the exact
# files that will be shipped (dist/) instead.
APP = ROOT / "src/SciBridge.App/bin/Release/net10.0-windows/win-x64/publish/SciBridge.exe"
SERVER = ROOT / ".build/SciBridgeTestServer.exe"
WORK = ROOT / ".testtmp-wine2"
PREFIX = ROOT / ".wineprefix-selftest"
RUNTIME = ROOT / ".winerun-selftest"

PASSWORD = "scibridge"

PASSED, FAILED = [], []


def check(name, ok, detail=""):
    (PASSED if ok else FAILED).append(name)
    print(("  PASS  " if ok else "  FAIL  ") + name)
    if not ok and detail:
        for line in str(detail).splitlines()[:60]:
            print("        " + line)


def wine_env():
    env = dict(os.environ)
    env["WINEPREFIX"] = str(PREFIX)
    env["WINEDEBUG"] = "-all"
    env["XDG_RUNTIME_DIR"] = str(RUNTIME)
    return env


NOISE = ("wine:", "err:", "warn:", "fixme:", "WARNING: radv", "ALSA", "wineserver", "dbus", "D-Bus")


def clean(text):
    return "\n".join(l for l in text.splitlines() if not l.strip().startswith(NOISE))


def win_path(p):
    return "Z:" + str(p).replace("/", "\\")


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def wait_port(port, timeout=45):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=2):
                return True
        except OSError:
            time.sleep(0.5)
    return False


def main():
    global APP, SERVER
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("--app", default=str(APP), help="要验证的 SciBridge.exe")
    parser.add_argument("--server", default=str(SERVER), help="要验证的 SciBridgeTestServer.exe")
    args = parser.parse_args()
    APP, SERVER = pathlib.Path(args.app), pathlib.Path(args.server)

    for path in (APP, SERVER):
        if not path.exists():
            print(f"missing build output: {path}")
            return 2

    if WORK.exists():
        shutil.rmtree(WORK)
    WORK.mkdir(parents=True)
    RUNTIME.mkdir(parents=True, exist_ok=True)
    os.chmod(RUNTIME, 0o700)

    port = free_port()
    op_log = WORK / "ops.jsonl"
    fingerprint_file = WORK / "fingerprint.txt"
    base = WORK / "data"

    print(f"\n=== Windows test server + Windows SciBridge self-test (port {port}) ===\n")

    server = subprocess.Popen(
        ["wine", str(SERVER), "-port", str(port), "-base", win_path(base),
         "-log", win_path(op_log), "-fingerprint-file", win_path(fingerprint_file), "-quiet"],
        env=wine_env(), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

    try:
        listening = wait_port(port)
        check("SciBridgeTestServer.exe starts and listens on 127.0.0.1", listening)
        if not listening:
            time.sleep(1)
            server.terminate()
            print(clean(server.stdout.read())[:2000])
            return 1

        check("test server seeded the sample data",
              (base / "sftp-root" / "README.txt").is_file(), str(base))

        # Run 1: first contact, so the host-key prompt must appear and be accepted.
        run1 = subprocess.run(
            ["wine", str(APP), "--self-test",
             "--host", "127.0.0.1", "--port", str(port),
             "--user", "test", "--root", ".", "--password", PASSWORD],
            env=wine_env(), input="y\n", capture_output=True, text=True, timeout=600)
        out1 = clean(run1.stdout)

        check("self-test reaches the server and reports success",
              run1.returncode == 0 and "全部通过" in out1,
              f"rc={run1.returncode}\n{out1}\n{clean(run1.stderr)[:600]}")
        check("first contact asks the user to confirm the host key",
              "接受并记住这个指纹" in out1 and "本次指纹 : SHA256:" in out1, out1[:900])

        # The fingerprint shown to the user must be the same string the server (or an
        # administrator) reports, otherwise verifying it by hand is impossible.
        server_fp = fingerprint_file.read_text(encoding="utf-8").strip()
        check("the displayed fingerprint matches the server's own fingerprint exactly",
              server_fp.startswith("SHA256:") and f"本次指纹 : {server_fp}" in out1,
              f"server reported {server_fp!r}")
        check("self-test exercises listing, reading, stat, find and health",
              all(word in out1 for word in ("health 返回服务信息", "列出可读根目录",
                                            "查看路径属性", "读取文件", "按文件名查找")),
              out1)
        check("self-test confirms traversal is refused", "拒绝越出可读根目录的路径" in out1, out1)
        check("self-test confirms no write operation exists", "不存在任何写入操作" in out1, out1)

        # Run 2: the fingerprint was pinned, so there must be no prompt this time.
        run2 = subprocess.run(
            ["wine", str(APP), "--self-test",
             "--host", "127.0.0.1", "--port", str(port),
             "--user", "test", "--root", ".", "--password", PASSWORD],
            env=wine_env(), input="", capture_output=True, text=True, timeout=600)
        out2 = clean(run2.stdout)

        check("second run succeeds without a host-key prompt (fingerprint was pinned)",
              run2.returncode == 0 and "全部通过" in out2
              and "接受并记住这个指纹" not in out2 and "第一次连接这台服务器" not in out2,
              f"rc={run2.returncode}\n{out2}")

        # The server's own log is the independent witness.
        rows = [json.loads(l) for l in op_log.read_text(encoding="utf-8").splitlines() if l.strip()]
        check(f"test server logged the sessions ({len(rows)} requests)",
              len(rows) > 5, "\n".join(json.dumps(r) for r in rows[:5]))
        check("no write request ever reached the test server",
              not any(r["op"].startswith("WRITE") for r in rows),
              "\n".join(json.dumps(r) for r in rows if r["op"].startswith("WRITE"))[:400])
        check("the write audit actually observed read traffic",
              any(r["op"] in ("read", "list", "lstat", "stat") for r in rows),
              "no read operations were logged, so the audit proves nothing")

        # A wrong password must fail cleanly and still only try once.
        before = len(rows)
        run3 = subprocess.run(
            ["wine", str(APP), "--self-test",
             "--host", "127.0.0.1", "--port", str(port),
             "--user", "test", "--root", ".", "--password", "not-the-password"],
            env=wine_env(), input="y\ny\n", capture_output=True, text=True, timeout=600)
        out3 = clean(run3.stdout)
        rows2 = [json.loads(l) for l in op_log.read_text(encoding="utf-8").splitlines() if l.strip()]
        auths = [r for r in rows2[before:] if r["op"] == "auth"]
        check("a wrong password fails with a clear message",
              run3.returncode != 0 and "认证失败" in out3, f"rc={run3.returncode}\n{out3[:600]}")
        check("a wrong password makes exactly one authentication attempt",
              len(auths) == 1, f"{len(auths)} attempts")

    finally:
        server.terminate()
        try:
            server.wait(timeout=15)
        except subprocess.TimeoutExpired:
            server.kill()
        for killer in (["wineboot", "-k"], ["wineserver", "-k"]):
            try:
                subprocess.run(killer, env=wine_env(), capture_output=True, timeout=30)
                break
            except (FileNotFoundError, subprocess.TimeoutExpired):
                continue

    print(f"\n=== {len(PASSED)} passed, {len(FAILED)} failed ===")
    for name in FAILED:
        print("  - " + name)
    return 1 if FAILED else 0


if __name__ == "__main__":
    sys.exit(main())

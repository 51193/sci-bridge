#!/usr/bin/env python3
"""
Verifies the shipped Windows executable itself, by running it under Wine against
the real password-authenticated SFTP test server, with a bridge served by the
Linux test host. This exercises the exact artifact that will be handed to the user:
its argument parsing, HTTP client, JSON decoding and console output on Windows.

Run:  <venv-python> tests/wine_check.py
Requires Wine (and permission for Wine to create its socket directory).
"""
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import sys
import time

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent
EXE = ROOT / "src/SciBridge.App/bin/Release/net10.0-windows/win-x64/publish/SciBridge.exe"
TESTHOST_DLL = ROOT / "src/SciBridge.TestHost/bin/Release/net10.0/SciBridge.TestHost.dll"
WORK = ROOT / ".testtmp-wine"
WINEPREFIX = ROOT / ".wineprefix"
RUNTIME_DIR = ROOT / ".winerun"

USER = "researcher"
PASSWORD = "wine-check-password"

PASSED, FAILED = [], []


def check(name, ok, detail=""):
    (PASSED if ok else FAILED).append(name)
    print(("  PASS  " if ok else "  FAIL  ") + name)
    if not ok and detail:
        for line in str(detail).splitlines()[:10]:
            print("        " + line)


def build_fixture(base):
    root = base / "root"
    outside = base / "outside"
    root.mkdir(parents=True, exist_ok=True)
    outside.mkdir(parents=True, exist_ok=True)
    (root / "README.txt").write_text("科研数据说明\n", encoding="utf-8")
    data = root / "data"
    data.mkdir(exist_ok=True)
    (data / "sample.txt").write_text("alpha\nbeta\n", encoding="utf-8")
    big = bytes((i * 37 + 11) % 256 for i in range(300_000))
    (data / "big.bin").write_bytes(big)
    deep = data / "deep" / "a" / "b" / "c"
    deep.mkdir(parents=True, exist_ok=True)
    (deep / "leaf.txt").write_text("leaf", encoding="utf-8")
    (outside / "secret.txt").write_text("OUTSIDE-SECRET", encoding="utf-8")
    os.symlink(str(outside), str(root / "link_out"))
    return root, big


def wine_env():
    env = dict(os.environ)
    env["WINEPREFIX"] = str(WINEPREFIX)
    env["WINEDEBUG"] = "-all"
    env["XDG_RUNTIME_DIR"] = str(RUNTIME_DIR)
    return env


def wine(*args, timeout=300):
    proc = subprocess.run(["wine", str(EXE), *args], capture_output=True, text=True,
                          env=wine_env(), timeout=timeout, errors="replace")
    noise = ("wine:", "err:", "warn:", "fixme:", "WARNING: radv", "ALSA", "wineserver")
    stderr = "\n".join(l for l in proc.stderr.splitlines() if not l.startswith(noise))
    return proc.returncode, proc.stdout, stderr


def main():
    if not EXE.exists():
        print(f"executable not found: {EXE}")
        return 2
    if not TESTHOST_DLL.exists():
        print(f"test host not found: {TESTHOST_DLL}")
        return 2

    if WORK.exists():
        shutil.rmtree(WORK)
    WORK.mkdir(parents=True)
    RUNTIME_DIR.mkdir(parents=True, exist_ok=True)
    os.chmod(RUNTIME_DIR, 0o700)

    root, big = build_fixture(WORK)
    ops_log = WORK / "ops.jsonl"

    sys.path.insert(0, str(HERE))
    from sftp_server import create_listener  # noqa: E402
    port, _fingerprint, stop_server = create_listener(
        str(root), USER, PASSWORD, log_path=str(ops_log))

    print(f"\n=== shipped SciBridge.exe under Wine (ssh server on {port}) ===\n")

    session = WORK / "session.json"
    known = WORK / "known_hosts.json"
    bridge = subprocess.Popen(
        ["dotnet", str(TESTHOST_DLL), "serve",
         "--host", "127.0.0.1", "--port", str(port), "--user", USER, "--password", PASSWORD,
         "--root", ".", "--session", str(session), "--known-hosts", str(known),
         "--exe-path", r"C:\Users\lab\Downloads\SciBridge.exe"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

    ready = False
    deadline = time.time() + 60
    while time.time() < deadline:
        line = bridge.stdout.readline()
        if not line:
            break
        if line.startswith("READY "):
            ready = True
        if line.startswith("----- END -----"):
            break

    try:
        check("bridge (linux core) is up for the windows client", ready)
        if not ready:
            print(bridge.stderr.read())
            return 1

        code, out, err = wine("--version")
        check("windows exe runs and reports its version",
              code == 0 and "SciBridge" in out, f"rc={code} out={out!r} err={err[:300]}")

        code, out, err = wine("--help")
        check("windows exe prints help", code == 0 and "health" in out, f"rc={code} {err[:200]}")

        code, out, err = wine("health", "--session", str(session))
        check("windows exe completes a full client/server round trip",
              code == 0 and "readOnly: true" in out, f"rc={code} out={out[:300]} err={err[:300]}")

        session_arg = str(session)
        code, out, err = wine("ls", ".", "--session", session_arg)
        check("windows exe lists a directory", code == 0 and "README.txt" in out and "data/" in out,
              f"rc={code} out={out[:300]} err={err[:300]}")

        code, out, err = wine("cat", "data/sample.txt", "--session", session_arg)
        check("windows exe reads text faithfully", code == 0 and out == "alpha\nbeta\n", repr(out[:200]))

        out_file = WORK / "wine_big.bin"
        code, out, err = wine("cat", "data/big.bin", "--out", str(out_file), "--session", session_arg)
        check("windows exe transfers a 300 KB binary byte-for-byte",
              code == 0 and out_file.exists() and
              hashlib.sha256(out_file.read_bytes()).hexdigest() == hashlib.sha256(big).hexdigest(),
              f"rc={code} {err[:200]}")

        code, out, err = wine("find", ".", "--glob", "*.txt", "--session", session_arg)
        check("windows exe finds nested files",
              code == 0 and "data/deep/a/b/c/leaf.txt" in out, f"rc={code} out={out[:300]}")

        code, out, err = wine("cat", "link_out/secret.txt", "--session", session_arg)
        check("windows exe refuses to read through a symlink",
              code != 0 and "符号链接" in err and "OUTSIDE-SECRET" not in out, f"rc={code} {err[:200]}")

        code, out, err = wine("ls", "../../outside", "--session", session_arg)
        check("windows exe refuses traversal", code != 0 and "越出" in err, f"rc={code} {err[:200]}")

        ops = [json.loads(l) for l in ops_log.read_text(encoding="utf-8").splitlines() if l.strip()]
        writes = [o for o in ops if o["op"].startswith("WRITE_")]
        check(f"no write operation reached the server ({len(ops)} requests)", not writes, writes[:3])
    finally:
        try:
            bridge.stdin.close()
            bridge.wait(timeout=15)
        except Exception:
            bridge.kill()
        stop_server()

    print(f"\n=== {len(PASSED)} passed, {len(FAILED)} failed ===")
    for name in FAILED:
        print("  - " + name)
    return 1 if FAILED else 0


if __name__ == "__main__":
    sys.exit(main())

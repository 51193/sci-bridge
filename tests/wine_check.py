#!/usr/bin/env python3
"""
Smoke-test the shipped Windows executable under Wine.

The agent-facing interface is now HTTP-only, so this no longer drives a CLI.
It checks that the artifact starts, prints help, and that its built-in
--self-test (connect -> start loopback HTTP bridge -> exercise every read-only
operation through HTTP) succeeds against a real SFTP server. The external HTTP
contract itself is covered by tests/e2e.py.

Run:  <venv-python> tests/wine_check.py
Requires Wine (and permission for Wine to create its socket directory).
"""
import json
import os
import pathlib
import shutil
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent
EXE = ROOT / "src/SciBridge.App/bin/Release/net10.0-windows/win-x64/publish/SciBridge.exe"
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


def wine(*args, timeout=600, input_text=""):
    proc = subprocess.run(
        ["wine", str(EXE), *args], capture_output=True, text=True,
        env=wine_env(), timeout=timeout, input=input_text, errors="replace")
    noise = ("wine:", "err:", "warn:", "fixme:", "WARNING: radv", "ALSA", "wineserver")
    stderr = "\n".join(l for l in proc.stderr.splitlines() if not l.startswith(noise))
    return proc.returncode, proc.stdout, stderr


def main():
    if not EXE.exists():
        print(f"executable not found: {EXE}")
        return 2

    if WORK.exists():
        shutil.rmtree(WORK)
    WORK.mkdir(parents=True)
    RUNTIME_DIR.mkdir(parents=True, exist_ok=True)
    os.chmod(RUNTIME_DIR, 0o700)

    root, _big = build_fixture(WORK)
    ops_log = WORK / "ops.jsonl"

    sys.path.insert(0, str(HERE))
    from sftp_server import create_listener  # noqa: E402
    port, _fingerprint, stop_server = create_listener(
        str(root), USER, PASSWORD, log_path=str(ops_log))

    print(f"\n=== shipped SciBridge.exe under Wine (ssh server on {port}) ===\n")

    try:
        code, out, err = wine("--version")
        check("windows exe runs and reports its version",
              code == 0 and "SciBridge" in out, f"rc={code} out={out!r} err={err[:300]}")

        code, out, err = wine("--help")
        check("windows exe explains that agents use HTTP, not the CLI",
              code == 0 and "curl.exe" in out and "--self-test" in out, f"rc={code} {err[:300]}")

        code, out, err = wine("health")
        check("removed CLI verbs point agents to the HTTP interface",
              code != 0 and "HTTP" in (out + err), f"rc={code} out={out!r} err={err[:300]}")

        code, out, err = wine(
            "--self-test", "--host", "127.0.0.1", "--port", str(port),
            "--user", USER, "--root", ".", "--password", PASSWORD,
            input_text="y\n")
        check("self-test completes the full HTTP read-only path",
              code == 0 and "全部通过" in out, f"rc={code}\n{out[-1500:]}\n{err[:400]}")
        check("self-test exercises health, listing, reading, stat and find",
              all(word in out for word in ("health 返回服务信息", "列出可读根目录",
                                           "查看路径属性", "读取文件", "按文件名查找")), out[-2000:])
        check("self-test confirms traversal is refused",
              "拒绝越出可读根目录的路径" in out, out[-1500:])
        check("self-test confirms no write operation exists",
              "不存在任何写入操作" in out, out[-1500:])

        rows = ops_log.read_text(encoding="utf-8").splitlines() if ops_log.exists() else []
        ops = [json.loads(l) for l in rows if l.strip()]
        writes = [o for o in ops if o["op"].startswith("WRITE_")]
        check(f"no write operation reached the server ({len(ops)} requests)", not writes, writes[:3])
    finally:
        stop_server()

    print(f"\n=== {len(PASSED)} passed, {len(FAILED)} failed ===")
    for name in FAILED:
        print("  - " + name)
    return 1 if FAILED else 0


if __name__ == "__main__":
    sys.exit(main())

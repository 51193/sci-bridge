#!/usr/bin/env python3
"""
Verifies the Go test server (tools/testserver) is a faithful stand-in for an
institutional host, by driving the real SciBridge Core against it over the same
loopback HTTP interface an agent uses.

Run:  <venv-python> tests/go_server_check.py [--server <path-to-testserver>]
"""
import argparse
import hashlib
import json
import pathlib
import shutil
import socket
import subprocess
import sys
import time

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent
TESTHOST_DLL = ROOT / "src/SciBridge.TestHost/bin/Release/net10.0/SciBridge.TestHost.dll"
WORK = ROOT / ".testtmp-go"

sys.path.insert(0, str(HERE))
from bridge_client import Bridge, json_call, read_all, request, text_call  # noqa: E402

USER = "test"
PASSWORD = "scibridge"

PASSED, FAILED = [], []


def check(name, ok, detail=""):
    (PASSED if ok else FAILED).append(name)
    print(("  PASS  " if ok else "  FAIL  ") + name)
    if not ok and detail:
        for line in str(detail).splitlines()[:10]:
            print("        " + line)


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--server", default=str(ROOT / ".build/testserver-linux"))
    args = parser.parse_args()

    server_exe = pathlib.Path(args.server)
    if not server_exe.exists():
        print(f"test server not built: {server_exe}")
        return 2
    if not TESTHOST_DLL.exists():
        print(f"test host not built: {TESTHOST_DLL}")
        return 2

    if WORK.exists():
        shutil.rmtree(WORK)
    WORK.mkdir(parents=True)

    port = free_port()
    base = WORK / "data"
    op_log = WORK / "ops.jsonl"

    server = subprocess.Popen(
        [str(server_exe), "-port", str(port), "-base", str(base), "-log", str(op_log)],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    time.sleep(2.0)

    print(f"\n=== Go test server on 127.0.0.1:{port} ===\n")
    known = WORK / "known_hosts.json"
    bridge = None
    sym = None

    try:
        if server.poll() is not None:
            print("server exited early:\n" + server.stdout.read())
            return 1

        root = base / "sftp-root"
        check("server seeded a data root", root.is_dir(), str(root))

        bridge = Bridge(port, known, user=USER, password=PASSWORD, dll=TESTHOST_DLL)
        check("SciBridge authenticates against the Go server", bridge.ready, bridge.startup_error)
        if not bridge.ready:
            return 1

        status, health, body = json_call(bridge, "health")
        check("health reports the test account, root and HTTP transport",
              status == 200 and health["user"] == USER and health["readOnly"] is True
              and health["rootReachable"] is True and health["transport"] == "http-loopback",
              body[:300])

        status, listing, body = json_call(bridge, "list", path=".")
        names = {e["name"]: e for e in (listing or {}).get("entries", [])}
        check("listing shows seeded entries with types",
              status == 200 and names.get("data", {}).get("type") == "dir"
              and names.get("README.txt", {}).get("type") == "file"
              and names.get("link_out", {}).get("type") == "link", body[:300])

        status, text, _ = text_call(bridge, "list", path=".")
        check("text listing uses ls -F style markers",
              status == 200 and "data/" in text and "README.txt" in text and "link_out@" in text,
              text[:300])

        status, text, _ = text_call(bridge, "read", path="README.txt")
        check("text read is byte-faithful (CRLF preserved exactly)",
              status == 200 and text.startswith("SciBridge 测试数据\r\n"), repr(text[:90]))

        status, text, _ = text_call(bridge, "read", path="data/observations.csv")
        check("CSV content reads correctly",
              status == 200 and "station,temperature_c" in text, text[:300])

        status, text, _ = text_call(bridge, "list", path="数据")
        check("unicode directory lists correctly",
              status == 200 and "观测记录.txt" in text, text[:300])

        status, found, body = json_call(
            bridge, "find", path=".", glob="*.txt", max=100)
        check("find reaches five levels deep",
              status == 200 and any(e["name"] == "data/deep/a/b/c/leaf.txt"
                                    for e in found["entries"]), body[:300])

        status, stat, body = json_call(bridge, "stat", path="link_out")
        check("LSTAT reports the symlink itself, not its target",
              status == 200 and stat["entry"]["type"] == "link", body[:300])

        status, text, _ = text_call(bridge, "list", path="link_out")
        check("listing through a symlink is refused by default",
              status == 400 and "符号链接" in text, text[:300])

        status, text, _ = text_call(bridge, "read", path="link_out/secret.txt")
        check("reading through a symlink is refused and leaks nothing",
              status == 400 and "这个文件在可读根目录之外" not in text
              and "OUTSIDE" not in text, text[:300])

        status, text, _ = text_call(bridge, "list", path="..")
        check("traversal above the root is refused",
              status == 400 and "越出" in text, text[:300])

        # Byte-exact transfer across several HTTP chunks.
        reference = (root / "data" / "big.bin").read_bytes()
        status, first, headers = request(
            bridge, "read", path="data/big.bin", offset=0, length=64 * 1024 * 1024)
        check("a single HTTP read is bounded to 4 MiB and reports size/eof",
              status == 200 and len(first) == 4 * 1024 * 1024
              and int(headers["X-SciBridge-Size"]) == len(reference)
              and headers.get("X-SciBridge-Eof") == "false",
              f"{len(first)} bytes, headers={headers}")

        got, size = read_all(bridge, "data/big.bin")
        check("9 MiB binary transfers byte-for-byte across several chunks",
              size == len(reference) and hashlib.sha256(got).hexdigest()
              == hashlib.sha256(reference).hexdigest(), f"{len(got)} bytes, size={size}")

        # ---------------------------------------------------- wrong password
        ops_before = len(op_log.read_text(encoding="utf-8").splitlines())
        bad = subprocess.run(
            ["dotnet", str(TESTHOST_DLL), "serve",
             "--host", "127.0.0.1", "--port", str(port), "--user", USER,
             "--password", "wrong-password", "--root", ".",
             "--known-hosts", str(known)],
            capture_output=True, text=True, timeout=180)
        rows = [json.loads(l) for l in op_log.read_text(encoding="utf-8").splitlines() if l.strip()]
        auths = [r for r in rows[ops_before:] if r["op"] == "auth"]
        check("a wrong password fails clearly",
              bad.returncode != 0 and "认证失败" in bad.stderr, bad.stderr[:200])
        check("a wrong password makes exactly one attempt",
              len(auths) == 1, f"{len(auths)} attempts")

        # ------------------------------------------------------- write audit
        writes = [r for r in rows if r["op"].startswith("WRITE")]
        check(f"the server saw no write attempt across {len(rows)} requests",
              not writes, writes[:3])

        # ---------------------------------------------------- symlink opt-in
        sym = Bridge(port, WORK / "known_sym.json", allow_symlinks=True,
                     user=USER, password=PASSWORD, dll=TESTHOST_DLL)
        check("second bridge starts with symlinks allowed", sym.ready, sym.startup_error)
        if sym.ready:
            status, text, _ = text_call(sym, "read", path="link_out/secret.txt")
            check("opting into symlinks is what unblocks the escape (default was the blocker)",
                  status == 200 and "这个文件在可读根目录之外" in text, text[:300])
        else:
            check("opting into symlinks is what unblocks the escape (default was the blocker)",
                  False, sym.startup_error)

    finally:
        for proc in (sym, bridge):
            if proc is not None:
                try:
                    proc.stop()
                except Exception:
                    pass
        server.terminate()
        try:
            server.wait(timeout=10)
        except subprocess.TimeoutExpired:
            server.kill()

    print(f"\n=== {len(PASSED)} passed, {len(FAILED)} failed ===")
    for name in FAILED:
        print("  - " + name)
    return 1 if FAILED else 0


if __name__ == "__main__":
    sys.exit(main())

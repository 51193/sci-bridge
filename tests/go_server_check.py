#!/usr/bin/env python3
"""
Verifies the Go test server (tools/testserver) is a faithful stand-in for an
institutional host, by driving the real SciBridge Core against it.

It checks the same properties the main suite checks, but with a completely
different server implementation, which is what makes it a useful cross-check:
if both servers satisfy the client's expectations, the client is not relying on
paramiko-specific quirks.

Run:  <venv-python> tests/go_server_check.py [--server <path-to-testserver>]
"""
import argparse
import base64
import hashlib
import json
import json as _json
import os
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


def cli_raw(session, *args, timeout=300):
    """Like cli(), but without newline translation, for byte-exact assertions."""
    cmd = ["dotnet", str(TESTHOST_DLL), args[0], *args[1:], "--session", str(session)]
    proc = subprocess.run(cmd, capture_output=True, timeout=timeout)
    return proc.returncode, proc.stdout, proc.stderr.decode("utf-8", "replace")


def cli(session, *args, timeout=300):
    cmd = ["dotnet", str(TESTHOST_DLL), args[0], *args[1:], "--session", str(session)]
    proc = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    return proc.returncode, proc.stdout, proc.stderr


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
    session = WORK / "session.json"
    known = WORK / "known_hosts.json"
    bridge = None

    try:
        if server.poll() is not None:
            print("server exited early:\n" + server.stdout.read())
            return 1

        root = base / "sftp-root"
        check("server seeded a data root", root.is_dir(), str(root))

        bridge = subprocess.Popen(
            ["dotnet", str(TESTHOST_DLL), "serve",
             "--host", "127.0.0.1", "--port", str(port), "--user", USER, "--password", PASSWORD,
             "--root", ".", "--session", str(session), "--known-hosts", str(known)],
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
        check("SciBridge authenticates against the Go server", ready,
              bridge.stderr.read() if not ready else "")
        if not ready:
            return 1

        code, out, err = cli(session, "health")
        check("health reports the test account and root",
              code == 0 and "user: " + USER in out and "readOnly: true" in out, out or err)

        code, out, err = cli(session, "ls", ".")
        check("listing shows seeded entries with type markers",
              code == 0 and "data/" in out and "README.txt" in out and "link_out@" in out, out or err)

        code, out, err = cli_raw(session, "cat", "README.txt")
        check("text read is byte-faithful (CRLF preserved exactly)",
              code == 0 and out.startswith("SciBridge 测试数据\r\n".encode("utf-8")),
              repr(out[:90]))

        code, out, err = cli(session, "cat", "data/observations.csv")
        check("CSV content reads correctly", code == 0 and "station,temperature_c" in out, out or err)

        code, out, err = cli(session, "ls", "数据")
        check("unicode directory lists correctly", code == 0 and "观测记录.txt" in out, out or err)

        code, out, err = cli(session, "find", ".", "--glob", "*.txt", "--max", "100")
        check("find reaches five levels deep", code == 0 and "data/deep/a/b/c/leaf.txt" in out, out or err)

        # The Go server implements LSTAT via LstatFileLister; if that were missing,
        # pkg/sftp would follow the link and this would report a directory instead.
        code, out, err = cli(session, "stat", "link_out")
        check("LSTAT reports the symlink itself, not its target",
              code == 0 and out.startswith("l") and "@" in out, out or err)

        code, out, err = cli(session, "ls", "link_out")
        check("listing through a symlink is refused by default",
              code != 0 and "符号链接" in err, err or out)

        code, out, err = cli(session, "cat", "link_out/secret.txt")
        check("reading through a symlink is refused and leaks nothing",
              code != 0 and "OUTSIDE" not in out and "根目录之外" not in out, err or out)

        code, out, err = cli(session, "ls", "..")
        check("traversal above the root is refused", code != 0 and "越出" in err, err or out)

        # Byte-exact comparison against the file the server actually created.
        reference = (root / "data" / "big.bin").read_bytes()
        out_file = WORK / "big_copy.bin"
        code, out, err = cli(session, "cat", "data/big.bin", "--out", str(out_file))
        check("9 MiB binary transfers byte-for-byte across several chunks",
              code == 0 and out_file.exists() and
              hashlib.sha256(out_file.read_bytes()).hexdigest() == hashlib.sha256(reference).hexdigest(),
              f"rc={code} {err[:200]}")

        # Printing to stdout must stay capped, or an agent could pull a whole
        # dataset into its context by accident.
        code, out, err = cli(session, "cat", "data/big.bin", "--b64")
        printed = base64.b64decode(out.strip()) if code == 0 else b""
        check("printing a file larger than the cap to stdout is capped and warns",
              code == 0 and len(printed) == 8 * 1024 * 1024 and "只读取了" in err,
              f"{len(printed)} bytes, stderr={err[:120]!r}")

        # ---------------------------------------------------- wrong password
        ops_before = len(op_log.read_text(encoding="utf-8").splitlines())
        bad = subprocess.run(
            ["dotnet", str(TESTHOST_DLL), "serve",
             "--host", "127.0.0.1", "--port", str(port), "--user", USER,
             "--password", "wrong-password", "--root", ".",
             "--session", str(WORK / "bad.json"), "--known-hosts", str(known)],
            capture_output=True, text=True, timeout=180)
        rows = [json.loads(l) for l in op_log.read_text(encoding="utf-8").splitlines() if l.strip()]
        auths = [r for r in rows[ops_before:] if r["op"] == "auth"]
        check("a wrong password fails clearly",
              bad.returncode != 0 and "认证失败" in bad.stderr, bad.stderr[:200])
        check("a wrong password makes exactly one attempt",
              len(auths) == 1, f"{len(auths)} attempts")

        # ------------------------------------------------------- write audit
        writes = [r for r in rows if r["op"].startswith("WRITE")]
        check(f"the server saw no write attempt across {len(rows)} requests", not writes, writes[:3])

        # ---------------------------------------------------- symlink opt-in
        sym = subprocess.Popen(
            ["dotnet", str(TESTHOST_DLL), "serve",
             "--host", "127.0.0.1", "--port", str(port), "--user", USER, "--password", PASSWORD,
             "--root", ".", "--allow-symlinks",
             "--session", str(WORK / "sym.json"), "--known-hosts", str(known)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        sym_ready = False
        deadline = time.time() + 60
        while time.time() < deadline:
            line = sym.stdout.readline()
            if not line:
                break
            if line.startswith("READY "):
                sym_ready = True
            if line.startswith("----- END -----"):
                break
        if sym_ready:
            code, out, err = cli(WORK / "sym.json", "cat", "link_out/secret.txt")
            check("opting into symlinks is what unblocks the escape (default was the blocker)",
                  code == 0 and "根目录之外" in out, err or out)
        else:
            check("opting into symlinks is what unblocks the escape (default was the blocker)",
                  False, sym.stderr.read())
        try:
            sym.stdin.close()
            sym.wait(timeout=15)
        except Exception:
            sym.kill()

    finally:
        if bridge is not None:
            try:
                bridge.stdin.close()
                bridge.wait(timeout=15)
            except Exception:
                bridge.kill()
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

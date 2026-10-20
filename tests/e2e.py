#!/usr/bin/env python3
"""
End-to-end tests for SciBridge.

These run the real Core library (via SciBridge.TestHost) against a real
password-authenticated SSH/SFTP server, so the SSH handshake, host-key trust,
path confinement, read-only guarantees and both transports are all exercised
for real rather than mocked.

Run:  <venv-python> tests/e2e.py
"""
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import sys
import time
import urllib.request

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent
TESTHOST_DLL = ROOT / "src/SciBridge.TestHost/bin/Release/net10.0/SciBridge.TestHost.dll"
WORK = ROOT / ".testtmp"

sys.path.insert(0, str(HERE))
from sftp_server import create_listener  # noqa: E402

USER = "researcher"
PASSWORD = "correct-horse-battery-staple"

PASSED, FAILED = [], []


def check(name, ok, detail=""):
    (PASSED if ok else FAILED).append(name)
    if ok:
        print(f"  PASS  {name}")
    else:
        print(f"  FAIL  {name}")
        if detail:
            for line in str(detail).splitlines()[:12]:
                print(f"        {line}")


def sha256(data):
    return hashlib.sha256(data).hexdigest()


# --------------------------------------------------------------------- fixture

def build_fixture(base):
    root = base / "root"
    outside = base / "outside"
    root.mkdir(parents=True, exist_ok=True)
    outside.mkdir(parents=True, exist_ok=True)

    (root / "README.txt").write_text("科研数据说明 first line\nsecond line\n", encoding="utf-8")
    data = root / "data"
    data.mkdir(exist_ok=True)
    (data / "sample.txt").write_text("alpha\nbeta\n", encoding="utf-8")
    (data / "empty.txt").write_bytes(b"")

    big = bytes((i * 37 + 11) % 256 for i in range(300_000))
    (data / "big.bin").write_bytes(big)

    deep = data / "deep" / "a" / "b" / "c"
    deep.mkdir(parents=True, exist_ok=True)
    (deep / "leaf.txt").write_text("leaf", encoding="utf-8")

    many = root / "many"
    many.mkdir(exist_ok=True)
    for i in range(50):
        (many / f"f{i:03d}.txt").write_text(str(i), encoding="utf-8")

    uni = root / "数据"
    uni.mkdir(exist_ok=True)
    (uni / "观测记录.txt").write_text("温度 21.5\n", encoding="utf-8")

    (outside / "secret.txt").write_text("OUTSIDE-SECRET", encoding="utf-8")

    # A symlink that leaves the access root: the main escape this tool must refuse.
    os.symlink(str(outside), str(root / "link_out"))
    # A symlink that stays inside: still refused by default, used to prove the opt-in.
    os.symlink(str(data), str(root / "link_in"))

    return root, outside, big


# ------------------------------------------------------------------- processes

class Bridge:
    def __init__(self, port, session, known_hosts, allow_symlinks=False, user=USER, password=PASSWORD,
                 exe_path="C:\\Users\\lab\\Downloads\\SciBridge.exe"):
        self.session = session
        cmd = [
            "dotnet", str(TESTHOST_DLL), "serve",
            "--host", "127.0.0.1", "--port", str(port),
            "--user", user, "--password", password,
            "--root", ".",
            "--session", str(session), "--known-hosts", str(known_hosts),
            "--exe-path", exe_path,
        ]
        if allow_symlinks:
            cmd.append("--allow-symlinks")
        self.proc = subprocess.Popen(
            cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.handoff = ""
        self.ready = False
        self.startup_error = ""
        deadline = time.time() + 60
        in_handoff = False
        while time.time() < deadline:
            line = self.proc.stdout.readline()
            if not line:
                break
            if line.startswith("READY "):
                self.ready = True
            elif line.startswith("----- HANDOFF -----"):
                in_handoff = True
            elif line.startswith("----- END -----"):
                break
            elif in_handoff:
                self.handoff += line
        if not self.ready:
            self.startup_error = self.proc.stderr.read()

    def stop(self):
        try:
            self.proc.stdin.close()
        except Exception:
            pass
        try:
            self.proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.proc.kill()


def cli(session, *args, timeout=180):
    cmd = ["dotnet", str(TESTHOST_DLL), args[0], *args[1:], "--session", str(session)]
    proc = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    return proc.returncode, proc.stdout, proc.stderr


def http_get(url, headers=None):
    request = urllib.request.Request(url, headers=headers or {})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(request, timeout=30) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()


# ------------------------------------------------------------------------ main

def main():
    if not TESTHOST_DLL.exists():
        print(f"TestHost not built: {TESTHOST_DLL}")
        return 2

    if WORK.exists():
        shutil.rmtree(WORK)
    WORK.mkdir(parents=True)
    root, outside, big = build_fixture(WORK)

    ops_log = WORK / "ops.jsonl"
    port, fingerprint, stop_server = create_listener(
        str(root), USER, PASSWORD, log_path=str(ops_log))

    print(f"\n=== SciBridge end-to-end (ssh server on 127.0.0.1:{port}) ===\n")

    session = WORK / "session.json"
    known = WORK / "known_hosts.json"
    mirror = WORK / "mirror"
    bridge = Bridge(port, session, known)
    symlink_bridge = None
    try:
        check("bridge starts and authenticates", bridge.ready, bridge.startup_error)
        if not bridge.ready:
            return 1

        info = json.loads(session.read_text(encoding="utf-8"))

        # ---------------------------------------------------------- security
        check("session file never contains the SSH password",
              PASSWORD not in session.read_text(encoding="utf-8"))
        check("handoff text never contains the session token",
              info["token"] not in bridge.handoff, bridge.handoff[:400])
        check("handoff text names the executable path",
              "SciBridge.exe" in bridge.handoff and "health" in bridge.handoff, bridge.handoff[:400])
        check("host key was pinned after first use",
              json.loads(known.read_text(encoding="utf-8"))["hosts"] != {})

        # ------------------------------------------------- auth attempt budget
        # Institutional accounts commonly lock after a few failures, so the tool
        # must never turn one wrong password into a burst of attempts.
        def auth_events():
            rows = [json.loads(l) for l in ops_log.read_text(encoding="utf-8").splitlines() if l.strip()]
            return [r for r in rows if r["op"].startswith("auth_")]

        before_auth = len(auth_events())
        bad_run = subprocess.run(
            ["dotnet", str(TESTHOST_DLL), "serve",
             "--host", "127.0.0.1", "--port", str(port),
             "--user", USER, "--password", "definitely-not-the-password",
             "--root", ".", "--session", str(WORK / "session_bad.json"),
             "--known-hosts", str(known)],
            capture_output=True, text=True, timeout=180)
        attempts = len(auth_events()) - before_auth

        check("a wrong password fails with an actionable message",
              bad_run.returncode != 0 and "认证失败" in bad_run.stderr, bad_run.stderr[:300])
        check("a wrong password produces exactly one authentication attempt",
              attempts == 1, f"{attempts} attempts reached the server")

        # ------------------------------------------------------------ health
        code, out, err = cli(session, "health")
        check("health succeeds", code == 0, err)
        check("health reports read-only", "readOnly: true" in out, out)
        check("health lists only read operations",
              all(op in out for op in ("health", "list", "stat", "read", "find")), out)

        # ------------------------------------------------------------ listing
        code, out, err = cli(session, "ls", ".")
        check("ls root succeeds", code == 0, err)
        check("ls root shows directories with trailing slash",
              "data/" in out and "README.txt" in out, out)

        code, out, err = cli(session, "ls", "data")
        check("ls subdirectory succeeds", code == 0 and "sample.txt" in out, out or err)

        code, out, err = cli(session, "ls", "many", "--max", "10")
        check("ls honours --max", code == 0 and "已截断" in out, out)

        # -------------------------------------------------------------- reads
        code, out, err = cli(session, "cat", "README.txt")
        check("cat text file is byte-faithful",
              code == 0 and out == "科研数据说明 first line\nsecond line\n", repr(out))

        code, out, err = cli(session, "cat", "data/empty.txt")
        check("cat empty file returns nothing", code == 0 and out == "", repr(out))

        out_file = WORK / "big_copy.bin"
        code, out, err = cli(session, "cat", "data/big.bin", "--out", str(out_file))
        check("cat --out preserves every byte of a 300 KB binary",
              code == 0 and out_file.exists() and sha256(out_file.read_bytes()) == sha256(big),
              err or out)

        code, out, err = cli(session, "cat", "data/big.bin", "--offset", "200", "--length", "1000", "--b64")
        sliced = b""
        if code == 0:
            import base64
            sliced = base64.b64decode(out.strip())
        check("offset/length read returns the exact slice",
              code == 0 and sliced == big[200:1200], f"{len(sliced)} bytes")

        code, out, err = cli(session, "stat", "data/big.bin")
        check("stat reports the real size", code == 0 and "300000" in out, out or err)

        code, out, err = cli(session, "stat", "link_in")
        check("stat identifies a symlink without following it",
              code == 0 and out.startswith("l") and "@" in out, out or err)

        code, out, err = cli(session, "cat", "data")
        check("cat on a directory is refused with guidance",
              code != 0 and "目录" in err, err)

        # ----------------------------------------------------------- unicode
        code, out, err = cli(session, "ls", "数据")
        check("unicode directory lists correctly", code == 0 and "观测记录.txt" in out, out or err)

        code, out, err = cli(session, "cat", "数据/观测记录.txt")
        check("unicode path reads correctly", code == 0 and "温度 21.5" in out, repr(out))

        # ---------------------------------------------------------- searching
        code, out, err = cli(session, "find", ".", "--glob", "*.txt", "--max", "100")
        check("find locates a file five levels deep using the default depth",
              code == 0 and "data/deep/a/b/c/leaf.txt" in out, out or err)

        code, out, err = cli(session, "find", ".", "--glob", "*.txt", "--max", "100", "--depth", "1")
        check("find --depth 1 stays shallow",
              code == 0 and "data/deep/a/b/c/leaf.txt" not in out, out or err)

        code, out, err = cli(session, "cat", "data/big.bin", "--max-bytes", "1000", "--b64")
        import base64 as _b64
        limited = _b64.b64decode(out.strip()) if code == 0 else b""
        check("cat --max-bytes caps the read and warns on stderr",
              code == 0 and limited == big[:1000] and "只读取了" in err, f"{len(limited)} {err[:120]}")

        code, out, err = cli(session, "find", ".", "--glob", "*.txt", "--max", "3", "--depth", "4")
        check("find honours --max and reports truncation",
              code == 0 and "已截断" in out, out)

        # ------------------------------------------------------- confinement
        for label, path in [
            ("parent traversal '..'", ".."),
            ("nested traversal", "../../outside"),
            ("traversal to a known file", "../../outside/secret.txt"),
            ("backslash traversal", "..\\..\\outside"),
        ]:
            code, out, err = cli(session, "ls", path)
            check(f"{label} is refused", code != 0 and "越出" in err, err)

        code, out, err = cli(session, "cat", "/etc/passwd")
        check("absolute path is treated as root-relative, not the real root",
              code != 0 and "不存在" in err, err or out)

        for label, path in [("symlink to outside", "link_out"),
                            ("file below a symlink to outside", "link_out/secret.txt")]:
            code, out, err = cli(session, "ls" if "secret" not in path else "cat", path)
            check(f"{label} is refused by default",
                  code != 0 and "符号链接" in err, err or out)
            check(f"{label} leaks no secret content", "OUTSIDE-SECRET" not in out, out)

        # ------------------------------------------------- direct HTTP access
        base = info["url"] + "/" + info["token"]
        status, body = http_get(base + "/list?path=.")
        check("HTTP list works with the token",
              status == 200 and b"README.txt" in body, f"{status} {body[:200]}")

        status, body = http_get(info["url"] + "/wrongtoken/list?path=.")
        check("HTTP rejects a wrong token", status == 401, f"{status} {body[:120]}")

        status, body = http_get(base + "/list?path=.", {"Origin": "https://evil.example"})
        check("HTTP rejects browser-originated requests", status == 403, f"{status} {body[:120]}")

        status, body = http_get(base + "/read?path=data/big.bin&offset=200&length=1000")
        check("HTTP raw read returns exact bytes",
              status == 200 and body == big[200:1200], f"{status} {len(body)} bytes")

        status, body = http_get(base + "/read?path=../../outside/secret.txt")
        check("HTTP also enforces the access root", status == 400 and b"OUTSIDE-SECRET" not in body,
              f"{status} {body[:160]}")

        # ------------------------------------------------------ pipe fallback
        broken = WORK / "session_pipe_only.json"
        pipe_info = dict(info)
        pipe_info["url"] = "http://127.0.0.1:1"   # a port nothing listens on
        broken.write_text(json.dumps(pipe_info), encoding="utf-8")
        code, out, err = cli(broken, "ls", "data")
        check("named-pipe fallback works when loopback TCP is unreachable",
              code == 0 and "sample.txt" in out, err or out)

        # ------------------------------------------------ write-op audit trail
        ops = [json.loads(line) for line in ops_log.read_text(encoding="utf-8").splitlines() if line.strip()]
        writes = [op for op in ops if op["op"].startswith("WRITE_") or op["op"] in ("chattr", "setstat")]
        check(f"server saw zero write operations across {len(ops)} requests",
              not writes, writes[:5])

        # ------------------------------------------------- symlink opt-in path
        symlink_bridge = Bridge(port, WORK / "session_sym.json", WORK / "known_sym.json", allow_symlinks=True)
        check("second bridge starts with symlinks allowed", symlink_bridge.ready, symlink_bridge.startup_error)
        if symlink_bridge.ready:
            code, out, err = cli(WORK / "session_sym.json", "cat", "link_out/secret.txt")
            check("allowing symlinks is what unblocks the escape (proves the default is the blocker)",
                  code == 0 and "OUTSIDE-SECRET" in out, err or out)

            code, out, err = cli(WORK / "session_sym.json", "ls", "..")
            check("even with symlinks allowed, '..' traversal stays refused",
                  code != 0, err or out)

        # ----------------------------------------------------- liveness error
        info_alive = info
        bridge.stop()
        symlink_bridge.stop()
        symlink_bridge = None
        time.sleep(0.5)

        code, out, err = cli(session, "health")
        check("a closed bridge yields an actionable error, not a hang",
              code != 0 and ("无法连接" in err or "重新连接" in err or "未找到" in err), err)

    finally:
        try:
            bridge.stop()
        except Exception:
            pass
        if symlink_bridge is not None:
            try:
                symlink_bridge.stop()
            except Exception:
                pass
        stop_server()

    print(f"\n=== {len(PASSED)} passed, {len(FAILED)} failed ===")
    if FAILED:
        print("failed:")
        for name in FAILED:
            print("  - " + name)
    return 1 if FAILED else 0


if __name__ == "__main__":
    sys.exit(main())

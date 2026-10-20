#!/usr/bin/env python3
"""
End-to-end tests for SciBridge's HTTP-only agent interface.

These run the real Core library (via SciBridge.TestHost) against a real
password-authenticated SSH/SFTP server, then exercise the same loopback HTTP
endpoint an agent uses. No CLI, no session file, no named pipe.
"""
import base64
import json
import os
import pathlib
import shutil
import subprocess
import sys
import time

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent
TESTHOST_DLL = ROOT / "src/SciBridge.TestHost/bin/Release/net10.0/SciBridge.TestHost.dll"
WORK = ROOT / ".testtmp"

sys.path.insert(0, str(HERE))
from sftp_server import create_listener  # noqa: E402
from bridge_client import (  # noqa: E402
    Bridge, json_call, raw_get, read_all, request, text_call, USER, PASSWORD,
)

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

    os.symlink(str(outside), str(root / "link_out"))
    os.symlink(str(data), str(root / "link_in"))
    return root, outside, big


def main():
    if not TESTHOST_DLL.exists():
        print(f"TestHost not built: {TESTHOST_DLL}")
        return 2

    if WORK.exists():
        shutil.rmtree(WORK)
    WORK.mkdir(parents=True)
    root, _outside, big = build_fixture(WORK)

    ops_log = WORK / "ops.jsonl"
    port, _fingerprint, stop_server = create_listener(
        str(root), USER, PASSWORD, log_path=str(ops_log))

    print(f"\n=== SciBridge HTTP end-to-end (ssh server on 127.0.0.1:{port}) ===\n")

    known = WORK / "known_hosts.json"
    bridge = Bridge(port, known)
    symlink_bridge = None
    try:
        check("bridge starts and authenticates", bridge.ready, bridge.startup_error)
        if not bridge.ready:
            return 1

        info = bridge.info

        # ---------------------------------------------------------- handoff
        check("READY never contains the SSH password", PASSWORD not in json.dumps(info))
        check("handoff carries the loopback URL and token",
              info["url"] in bridge.handoff and info["token"] in bridge.handoff,
              bridge.handoff[:500])
        check("handoff asks the agent to request local-network permission",
              "允许本机网络访问" in bridge.handoff or "本机回环网络" in bridge.handoff,
              bridge.handoff[:500])
        check("handoff gives copy-pasteable curl.exe commands",
              "curl.exe" in bridge.handoff and "--noproxy" in bridge.handoff
              and "--fail-with-body" in bridge.handoff, bridge.handoff[:500])
        check("handoff no longer points at an executable or a session file",
              "SciBridge.exe" not in bridge.handoff and "session.json" not in bridge.handoff,
              bridge.handoff[:500])
        check("handoff no longer mentions the named pipe", "管道" not in bridge.handoff)
        check("session descriptor no longer exposes a pipe or an executable path",
              "pipe" not in info and "exePath" not in info, info)
        check("host key was pinned after first use",
              json.loads(known.read_text(encoding="utf-8"))["hosts"] != {})

        # ------------------------------------------------- auth attempt budget
        def auth_events():
            rows = [json.loads(l) for l in ops_log.read_text(encoding="utf-8").splitlines() if l.strip()]
            return [r for r in rows if r["op"].startswith("auth_")]

        before_auth = len(auth_events())
        bad_run = subprocess.run(
            ["dotnet", str(TESTHOST_DLL), "serve",
             "--host", "127.0.0.1", "--port", str(port),
             "--user", USER, "--password", "definitely-not-the-password",
             "--root", ".", "--known-hosts", str(known)],
            capture_output=True, text=True, timeout=180)
        attempts = len(auth_events()) - before_auth

        check("a wrong password fails with an actionable message",
              bad_run.returncode != 0 and "认证失败" in bad_run.stderr, bad_run.stderr[:300])
        check("a wrong password produces exactly one authentication attempt",
              attempts == 1, f"{attempts} attempts reached the server")

        # ------------------------------------------------------------ health
        status, health, body = json_call(bridge, "health")
        check("health succeeds", status == 200, body[:300])
        check("health reports read-only and a reachable root",
              health is not None and health.get("readOnly") is True
              and health.get("rootReachable") is True, health)
        check("health reports the HTTP transport",
              health is not None and health.get("transport") == "http-loopback", health)
        check("health lists only read operations",
              health is not None and all(op in health.get("operations", [])
                                         for op in ("health", "list", "stat", "read", "find")), health)
        check("health uses the version embedded in the session object",
              health is not None and bool(info.get("appVersion"))
              and health.get("version") == info.get("appVersion"), health)

        status, text, _ = text_call(bridge, "health")
        check("health text is available for humans",
              status == 200 and "readOnly: true" in text and "rootReachable: true" in text, text[:300])

        # ------------------------------------------------------------ listing
        status, listing, body = json_call(bridge, "list", path=".")
        names = {e["name"]: e for e in (listing or {}).get("entries", [])}
        check("list root succeeds", status == 200 and "README.txt" in names, body[:300])
        check("list reports directories and files with types",
              names.get("data", {}).get("type") == "dir"
              and names.get("README.txt", {}).get("type") == "file", names)

        status, text, _ = text_call(bridge, "list", path=".")
        check("text listing uses ls -F style markers",
              status == 200 and "data/" in text and "README.txt" in text, text[:300])

        status, sub, body = json_call(bridge, "list", path="data")
        check("list subdirectory succeeds",
              status == 200 and any(e["name"] == "sample.txt" for e in sub["entries"]), body[:300])

        status, many, body = json_call(bridge, "list", path="many", max=10)
        check("list honours max and reports truncation",
              status == 200 and many["count"] == 10 and many["truncated"] is True, many)

        # -------------------------------------------------------------- reads
        status, text, _ = text_call(bridge, "read", path="README.txt")
        check("text read is byte-faithful",
              status == 200 and text == "科研数据说明 first line\nsecond line\n", repr(text))

        status, text, _ = text_call(bridge, "read", path="data/empty.txt")
        check("empty file reads as empty", status == 200 and text == "", repr(text))

        raw, size = read_all(bridge, "data/big.bin")
        check("raw read preserves every byte of a 300 KB binary",
              size == len(big) and raw == big, f"{len(raw)} bytes, size={size}")

        status, sliced, headers = request(
            bridge, "read", path="data/big.bin", offset=200, length=1000)
        check("offset/length read returns the exact slice",
              status == 200 and sliced == big[200:1200], f"{len(sliced)} bytes")
        check("raw read exposes offset/size/eof headers",
              headers.get("X-SciBridge-Offset") == "200"
              and headers.get("X-SciBridge-Size") == str(len(big))
              and headers.get("X-SciBridge-Eof") == "false", headers)

        status, read_json, body = json_call(
            bridge, "read", path="data/big.bin", offset=200, length=1000)
        decoded = base64.b64decode(read_json["data"]) if read_json else b""
        check("JSON read returns base64 and metadata",
              status == 200 and decoded == big[200:1200]
              and read_json["encoding"] == "base64" and read_json["size"] == len(big), read_json)

        status, stat, body = json_call(bridge, "stat", path="data/big.bin")
        check("stat reports the real size",
              status == 200 and stat["entry"]["size"] == len(big), body[:300])

        status, stat, body = json_call(bridge, "stat", path="link_in")
        check("stat identifies a symlink without following it",
              status == 200 and stat["entry"]["type"] == "link" and stat["entry"]["symlink"] is True,
              body[:300])

        status, text, body = text_call(bridge, "read", path="data")
        check("reading a directory is refused with guidance",
              status == 400 and "目录" in text, text[:300])

        # ----------------------------------------------------------- unicode
        status, listing, body = json_call(bridge, "list", path="数据")
        check("unicode directory lists correctly",
              status == 200 and any(e["name"] == "观测记录.txt" for e in listing["entries"]),
              body[:300])

        status, text, _ = text_call(bridge, "read", path="数据/观测记录.txt")
        check("unicode path reads correctly", status == 200 and "温度 21.5" in text, repr(text))

        # ---------------------------------------------------------- searching
        status, found, body = json_call(bridge, "find", path=".", glob="*.txt", max=100)
        check("find locates a file five levels deep using the default depth",
              status == 200 and any(e["name"] == "data/deep/a/b/c/leaf.txt"
                                    for e in found["entries"]), body[:300])

        status, found, body = json_call(
            bridge, "find", path=".", glob="*.txt", max=100, depth=1)
        check("find depth=1 stays shallow",
              status == 200 and not any(e["name"] == "data/deep/a/b/c/leaf.txt"
                                        for e in found["entries"]), body[:300])

        status, found, body = json_call(bridge, "find", path=".", glob="*.txt", max=3, depth=4)
        check("find honours max and reports truncation",
              status == 200 and found["count"] == 3 and found["truncated"] is True, found)

        # ------------------------------------------------------- confinement
        for label, path in [
            ("parent traversal '..'", ".."),
            ("nested traversal", "../../outside"),
            ("traversal to a known file", "../../outside/secret.txt"),
            ("backslash traversal", "..\\..\\outside"),
        ]:
            status, text, _ = text_call(bridge, "list", path=path)
            check(f"{label} is refused", status == 400 and "越出" in text, text[:300])

        status, text, _ = text_call(bridge, "read", path="/etc/passwd")
        check("absolute path is treated as root-relative, not the real root",
              status == 400 and "不存在" in text, text[:300])

        for label, path, op in [
            ("symlink to outside", "link_out", "list"),
            ("file below a symlink to outside", "link_out/secret.txt", "read"),
        ]:
            status, text, _ = text_call(bridge, op, path=path)
            check(f"{label} is refused by default",
                  status == 400 and "符号链接" in text, text[:300])
            check(f"{label} leaks no secret content", "OUTSIDE-SECRET" not in text, text[:300])

        # ------------------------------------------------- direct HTTP access
        status, body, _ = raw_get(bridge.url + "/wrongtoken/list?path=.")
        check("HTTP rejects a wrong token", status == 401, f"{status} {body[:120]}")

        status, body, _ = raw_get(bridge.url + "/health")
        check("HTTP requires the token in the URL path", status == 404, f"{status} {body[:120]}")

        status, body, _ = request(bridge, "list", path=".", headers={"Origin": "https://evil.example"})
        check("HTTP rejects browser-originated requests", status == 403, f"{status} {body[:120]}")

        status, body, _ = request(bridge, "health", method="HEAD")
        check("HTTP health is GET-only (HEAD is refused)", status == 405, f"{status} {body[:120]}")

        status, body, _ = request(bridge, "read", path="../../outside/secret.txt")
        check("HTTP also enforces the access root",
              status == 400 and b"OUTSIDE-SECRET" not in body, f"{status} {body[:160]}")

        # ------------------------------------------------ write-op audit trail
        ops = [json.loads(line) for line in ops_log.read_text(encoding="utf-8").splitlines() if line.strip()]
        writes = [op for op in ops if op["op"].startswith("WRITE_") or op["op"] in ("chattr", "setstat")]
        check(f"server saw zero write operations across {len(ops)} requests",
              not writes, writes[:5])

        # ------------------------------------------------- symlink opt-in path
        symlink_bridge = Bridge(port, WORK / "known_sym.json", allow_symlinks=True)
        check("second bridge starts with symlinks allowed",
              symlink_bridge.ready, symlink_bridge.startup_error)
        if symlink_bridge.ready:
            status, text, _ = text_call(symlink_bridge, "read", path="link_out/secret.txt")
            check("allowing symlinks is what unblocks the escape (proves the default is the blocker)",
                  status == 200 and "OUTSIDE-SECRET" in text, text[:300])

            status, text, _ = text_call(symlink_bridge, "list", path="..")
            check("even with symlinks allowed, '..' traversal stays refused",
                  status == 400, text[:300])

        # ----------------------------------------------------- liveness error
        bridge.stop()
        if symlink_bridge is not None:
            symlink_bridge.stop()
            symlink_bridge = None
        time.sleep(0.5)

        status, body, _ = raw_get(f"{bridge.url}/{bridge.token}/health")
        check("a closed bridge yields a transport error, not a hang",
              status == 0, f"{status} {body[:200]}")

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

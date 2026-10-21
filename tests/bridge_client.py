#!/usr/bin/env python3
"""Shared helpers for the HTTP-only SciBridge tests.

The bridge process under test is SciBridge.TestHost ("serve" mode). It prints one
READY line containing the session JSON (loopback URL + short-lived token) and the
handoff text. Tests talk to it exactly like an agent would: with HTTP GET requests
to 127.0.0.1, never by executing a CLI or reading a session file.
"""
import json
import os
import pathlib
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request

USER = "researcher"
PASSWORD = "correct-horse-battery-staple"
DEFAULT_DLL = pathlib.Path(__file__).resolve().parent.parent / "src/SciBridge.TestHost/bin/Release/net10.0/SciBridge.TestHost.dll"


class Bridge:
    def __init__(self, ssh_port, known_hosts, allow_symlinks=False, user=USER,
                 password=PASSWORD, root=".", dll=DEFAULT_DLL,
                 allow_upload=False, allow_mkdir=False, data_home=None):
        cmd = [
            "dotnet", str(dll), "serve",
            "--host", "127.0.0.1", "--port", str(ssh_port),
            "--user", user, "--password", password,
            "--root", root,
            "--known-hosts", str(known_hosts),
        ]
        if allow_symlinks:
            cmd.append("--allow-symlinks")
        if allow_upload:
            cmd.append("--allow-upload")
        if allow_mkdir:
            cmd.append("--allow-mkdir")
        env = None
        if data_home is not None:
            env = dict(os.environ)
            env["XDG_DATA_HOME"] = str(data_home)
        self.proc = subprocess.Popen(
            cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, env=env)
        self.info = None
        self.handoff = ""
        self.startup_error = ""
        deadline = time.time() + 60
        in_handoff = False
        while time.time() < deadline:
            line = self.proc.stdout.readline()
            if not line:
                break
            if line.startswith("READY "):
                self.info = json.loads(line[len("READY "):].strip())
            elif line.startswith("----- HANDOFF -----"):
                in_handoff = True
            elif line.startswith("----- END -----"):
                break
            elif in_handoff:
                self.handoff += line
        if self.info is None:
            self.startup_error = self.proc.stderr.read()
            self.stop()

    @property
    def ready(self):
        return self.info is not None

    @property
    def url(self):
        return self.info["url"]

    @property
    def token(self):
        return self.info["token"]

    @property
    def base_url(self):
        return self.url.rstrip("/") + "/" + self.token + "/"

    def stop(self):
        try:
            self.proc.stdin.close()
        except Exception:
            pass
        try:
            self.proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.proc.kill()


def raw_request(url, headers=None, method="GET", data=None, timeout=30):
    """Returns (status, body, headers). Status 0 means the connection failed."""
    req = urllib.request.Request(url, data=data, headers=headers or {}, method=method)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(req, timeout=timeout) as response:
            return response.status, response.read(), dict(response.headers)
    except urllib.error.HTTPError as error:
        return error.code, error.read(), dict(error.headers)
    except urllib.error.URLError as error:
        return 0, str(error).encode("utf-8", "replace"), {}


def raw_get(url, headers=None, method="GET", timeout=30):
    return raw_request(url, headers=headers, method=method, timeout=timeout)


def request(bridge, op, headers=None, method="GET", timeout=30, data=None, **params):
    query = urllib.parse.urlencode(params, quote_via=urllib.parse.quote)
    url = f"{bridge.url}/{bridge.token}/{op}"
    if query:
        url += "?" + query
    return raw_request(url, headers=headers, method=method, data=data, timeout=timeout)


def upload(bridge, path, data, format_name="text"):
    query = urllib.parse.urlencode(
        {"path": path, "format": format_name}, quote_via=urllib.parse.quote)
    return raw_request(
        f"{bridge.base_url}upload?{query}",
        headers={"Content-Type": "application/octet-stream"},
        method="PUT", data=data)


def mkdir(bridge, path, format_name="text"):
    query = urllib.parse.urlencode(
        {"path": path, "format": format_name}, quote_via=urllib.parse.quote)
    return raw_request(
        f"{bridge.base_url}mkdir?{query}",
        headers={"Content-Type": "application/octet-stream"},
        method="POST", data=b"")


def json_call(bridge, op, **params):
    params["format"] = "json"
    status, body, headers = request(bridge, op, **params)
    if status != 200:
        return status, None, body
    return status, json.loads(body), body


def text_call(bridge, op, **params):
    params.setdefault("format", "text")
    status, body, headers = request(bridge, op, **params)
    return status, body.decode("utf-8", "replace"), body


def read_all(bridge, path, chunk=4 * 1024 * 1024):
    """Follows offset/length until the bridge reports EOF; returns raw bytes."""
    data = bytearray()
    offset = 0
    size = None
    while True:
        status, body, headers = request(
            bridge, "read", path=path, offset=offset, length=chunk)
        if status != 200:
            raise RuntimeError(f"read {path!r} failed: {status} {body[:200]!r}")
        size = int(headers.get("X-SciBridge-Size", "-1"))
        data.extend(body)
        offset += len(body)
        eof = headers.get("X-SciBridge-Eof", "").lower() == "true"
        if eof or not body or (size >= 0 and offset >= size):
            break
    return bytes(data), size

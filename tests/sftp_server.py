#!/usr/bin/env python3
"""
A minimal SSH/SFTP server used to test SciBridge. Read-only by default; tests
can explicitly enable the new-file/mkdir operations.

It behaves like an institutional server for the parts that matter here:
  * password (and optionally keyboard-interactive) authentication
  * a virtual SFTP root that refuses to leave its directory
  * LSTAT-style attributes in listings, so symlinks are visible as symlinks
  * every request is appended to a JSONL log, which lets the test assert that
    the client never issued a single write operation

Standalone usage:
  sftp_server.py --port 2222 --root /path/to/data --user researcher \
                 --password secret --log /tmp/ops.jsonl
"""
import argparse
import errno
import json
import os
import socket
import sys
import threading
import time

import paramiko


def log_op(log_path, entry):
    if not log_path:
        return
    entry["t"] = time.time()
    with open(log_path, "a", encoding="utf-8") as handle:
        handle.write(json.dumps(entry) + "\n")


class ReadOnlySftpServer(paramiko.SFTPServerInterface):
    """Maps virtual absolute paths onto a real directory, read-only."""

    def __init__(self, server, *args, **kwargs):
        self.root = server.root
        self.log_path = server.log_path
        self.allow_write = getattr(server, "allow_write", False)
        super().__init__(server, *args, **kwargs)

    # ---------------------------------------------------------------- paths

    def _virtual(self, path):
        path = (path or ".").replace("\\", "/")
        if not path.startswith("/"):
            path = "/" + path
        parts = []
        for segment in path.split("/"):
            if segment in ("", "."):
                continue
            if segment == "..":
                if not parts:
                    raise IOError(errno.EACCES, "escape above root")
                parts.pop()
                continue
            parts.append(segment)
        return "/" + "/".join(parts)

    def _real(self, path):
        virtual = self._virtual(path)
        real = os.path.normpath(os.path.join(self.root, virtual.lstrip("/")))
        if real != self.root and not real.startswith(self.root + os.sep):
            raise IOError(errno.EACCES, "outside root")
        return real

    def canonicalize(self, path):
        return self._virtual(path)

    # ----------------------------------------------------------- read-only

    def list_folder(self, path):
        real = self._real(path)
        log_op(self.log_path, {"op": "list_folder", "path": path})
        out = []
        for name in sorted(os.listdir(real)):
            full = os.path.join(real, name)
            try:
                attr = paramiko.SFTPAttributes.from_stat(os.lstat(full))
            except OSError:
                continue
            attr.filename = name
            out.append(attr)
        return out

    def stat(self, path):
        log_op(self.log_path, {"op": "stat", "path": path})
        try:
            return paramiko.SFTPAttributes.from_stat(os.stat(self._real(path)))
        except FileNotFoundError:
            raise IOError(errno.ENOENT, "no such file")

    def lstat(self, path):
        log_op(self.log_path, {"op": "lstat", "path": path})
        try:
            return paramiko.SFTPAttributes.from_stat(os.lstat(self._real(path)))
        except FileNotFoundError:
            raise IOError(errno.ENOENT, "no such file")

    def open(self, path, flags, attr):
        write_flags = flags & (os.O_WRONLY | os.O_RDWR | os.O_APPEND | os.O_CREAT | os.O_TRUNC)
        if write_flags:
            log_op(self.log_path, {"op": "WRITE_OPEN", "path": path, "flags": flags})
            if not self.allow_write:
                raise IOError(errno.EACCES, "this server is read-only")
            real = self._real(path)
            mode = "ab" if flags & os.O_APPEND else "wb" if flags & os.O_TRUNC else "r+b"
            try:
                handle = paramiko.SFTPHandle(flags)
                handle.filename = real
                handle.writefile = open(real, mode)
                if flags & os.O_RDWR:
                    handle.readfile = open(real, "rb")
                return handle
            except OSError as exc:
                raise IOError(exc.errno or errno.EACCES, str(exc))
        log_op(self.log_path, {"op": "open_read", "path": path})
        handle = paramiko.SFTPHandle(flags)
        handle.filename = self._real(path)
        handle.readfile = open(handle.filename, "rb")
        return handle

    def readlink(self, path):
        log_op(self.log_path, {"op": "readlink", "path": path})
        return os.readlink(self._real(path))

    # ------------------------------------------------------- write blockers

    def _refuse(self, op, path):
        log_op(self.log_path, {"op": "WRITE_" + op.upper(), "path": path})
        raise IOError(errno.EACCES, "this server is read-only (%s)" % op)

    def remove(self, path):
        log_op(self.log_path, {"op": "WRITE_REMOVE", "path": path})
        if not self.allow_write:
            raise IOError(errno.EACCES, "this server is read-only (remove)")
        os.remove(self._real(path))
        return paramiko.SFTP_OK

    def rename(self, oldpath, newpath):
        log_op(self.log_path, {"op": "WRITE_RENAME", "old": oldpath, "new": newpath})
        if not self.allow_write:
            raise IOError(errno.EACCES, "this server is read-only (rename)")
        os.rename(self._real(oldpath), self._real(newpath))
        return paramiko.SFTP_OK

    def mkdir(self, path, attr):
        log_op(self.log_path, {"op": "WRITE_MKDIR", "path": path})
        if not self.allow_write:
            raise IOError(errno.EACCES, "this server is read-only (mkdir)")
        os.mkdir(self._real(path))
        return paramiko.SFTP_OK

    def rmdir(self, path):
        log_op(self.log_path, {"op": "WRITE_RMDIR", "path": path})
        if not self.allow_write:
            raise IOError(errno.EACCES, "this server is read-only (rmdir)")
        os.rmdir(self._real(path))
        return paramiko.SFTP_OK

    def chattr(self, path, attr):
        log_op(self.log_path, {"op": "WRITE_CHATTR", "path": path})
        if not self.allow_write:
            raise IOError(errno.EACCES, "this server is read-only (chattr)")
        return paramiko.SFTP_OK

    def symlink(self, target_path, path):
        log_op(self.log_path, {"op": "WRITE_SYMLINK", "path": path})
        if not self.allow_write:
            raise IOError(errno.EACCES, "this server is read-only (symlink)")
        os.symlink(self._real(target_path), self._real(path))
        return paramiko.SFTP_OK

    def setstat(self, path, attr):
        log_op(self.log_path, {"op": "WRITE_SETSTAT", "path": path})
        if not self.allow_write:
            raise IOError(errno.EACCES, "this server is read-only (setstat)")
        return paramiko.SFTP_OK


class AuthServer(paramiko.ServerInterface):
    def __init__(self, root, log_path, user, password, allow_keyboard_interactive,
                 allow_write=False):
        self.root = root
        self.log_path = log_path
        self.user = user
        self.password = password
        self.allow_keyboard_interactive = allow_keyboard_interactive
        self.allow_write = allow_write
        self.pending_user = None

    def check_auth_password(self, username, password):
        ok = username == self.user and password == self.password
        log_op(self.log_path, {"op": "auth_password", "user": username, "ok": ok})
        return paramiko.AUTH_SUCCESSFUL if ok else paramiko.AUTH_FAILED

    def check_auth_interactive(self, username, submethods):
        log_op(self.log_path, {"op": "auth_keyboard_interactive", "user": username})
        if not self.allow_keyboard_interactive or username != self.user:
            return paramiko.AUTH_FAILED
        self.pending_user = username
        return paramiko.AUTH_PARTIALLY_SUCCESSFUL

    def check_auth_interactive_response(self, responses):
        ok = bool(self.pending_user and responses and responses[0] == self.password)
        log_op(self.log_path, {"op": "auth_ki_response", "ok": ok})
        return paramiko.AUTH_SUCCESSFUL if ok else paramiko.AUTH_FAILED

    def get_allowed_auths(self, username):
        methods = ["password"]
        if self.allow_keyboard_interactive:
            methods.append("keyboard-interactive")
        return ",".join(methods)

    def check_channel_request(self, kind, chanid):
        if kind == "session":
            return paramiko.OPEN_SUCCEEDED
        return paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED


def serve_connection(client_socket, host_key, root, log_path, user, password,
                     allow_keyboard_interactive, allow_write):
    transport = paramiko.Transport(client_socket)
    transport.add_server_key(host_key)
    transport.set_subsystem_handler("sftp", paramiko.SFTPServer, ReadOnlySftpServer)

    auth = AuthServer(root, log_path, user, password, allow_keyboard_interactive, allow_write)
    try:
        transport.start_server(server=auth)
    except Exception as exc:  # noqa: BLE001
        log_op(log_path, {"op": "server_error", "error": str(exc)})
        transport.close()
        return

    while transport.is_active():
        time.sleep(0.2)
    transport.close()


def create_listener(root, user, password, log_path=None, port=0,
                    keyboard_interactive=False, key_bits=2048, allow_write=False):
    """Starts an in-process SFTP server. Returns (port, fingerprint, stop)."""
    host_key = paramiko.RSAKey.generate(key_bits)
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", port))
    listener.listen(16)
    listener.settimeout(0.5)
    actual_port = listener.getsockname()[1]
    fingerprint = "SHA256:" + host_key.fingerprint
    stop_flag = threading.Event()
    root = os.path.abspath(root)

    def loop():
        while not stop_flag.is_set():
            try:
                client_socket, _ = listener.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            threading.Thread(
                target=serve_connection,
                args=(client_socket, host_key, root, log_path, user, password,
                      keyboard_interactive, allow_write),
                daemon=True,
            ).start()

    threading.Thread(target=loop, daemon=True).start()

    def stop():
        stop_flag.set()
        try:
            listener.close()
        except OSError:
            pass

    return actual_port, fingerprint, stop


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--root", required=True)
    parser.add_argument("--user", required=True)
    parser.add_argument("--password", required=True)
    parser.add_argument("--log")
    parser.add_argument("--keyboard-interactive", action="store_true")
    parser.add_argument("--allow-write", action="store_true")
    parser.add_argument("--fingerprint-file")
    args = parser.parse_args()

    port, fingerprint, stop = create_listener(
        args.root, args.user, args.password, args.log, args.port,
        args.keyboard_interactive)

    if args.fingerprint_file:
        with open(args.fingerprint_file, "w", encoding="utf-8") as handle:
            handle.write(fingerprint)

    print(json.dumps({"ready": True, "port": port, "fingerprint": fingerprint}), flush=True)
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        stop()
    return 0


if __name__ == "__main__":
    sys.exit(main())

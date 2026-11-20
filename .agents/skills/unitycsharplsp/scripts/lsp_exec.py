#!/usr/bin/env python3
"""Run a UnityCSharpLSP workspace/executeCommand from the shell over LSP stdio."""

import argparse
import json
import queue
import subprocess
import sys
import threading
import time
from pathlib import Path


def frame(message: dict) -> bytes:
    body = json.dumps(message, ensure_ascii=False).encode("utf-8")
    return f"Content-Length: {len(body)}\r\n\r\n".encode("ascii") + body


class LspClient:
    def __init__(self, exe: str, root: str) -> None:
        self.process = subprocess.Popen(
            [exe, "--stdio"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        assert self.process.stdin is not None
        assert self.process.stdout is not None
        self.stdin = self.process.stdin
        self.messages: queue.Queue = queue.Queue()
        self.next_id = 1
        self.root_uri = Path(root).resolve().as_uri()
        threading.Thread(target=self._read_loop, daemon=True).start()

    def _read_loop(self) -> None:
        stream = self.process.stdout
        assert stream is not None
        while True:
            headers = {}
            while True:
                line = stream.readline()
                if not line:
                    self.messages.put(None)
                    return
                if line in (b"\r\n", b"\n"):
                    break
                if b":" in line:
                    key, value = line.decode("utf-8", "replace").split(":", 1)
                    headers[key.strip().lower()] = value.strip()
            length = int(headers.get("content-length", "0"))
            if length == 0:
                continue
            data = stream.read(length)
            if not data:
                self.messages.put(None)
                return
            try:
                self.messages.put(json.loads(data.decode("utf-8")))
            except json.JSONDecodeError:
                pass

    def send(self, method: str, params=None, message_id=None) -> None:
        message = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        if message_id is not None:
            message["id"] = message_id
        self.stdin.write(frame(message))
        self.stdin.flush()

    def respond(self, message_id, result=None) -> None:
        message = {"jsonrpc": "2.0", "id": message_id, "result": result}
        self.stdin.write(frame(message))
        self.stdin.flush()

    def wait_for(self, predicate, timeout: float) -> dict:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                message = self.messages.get(timeout=0.25)
            except queue.Empty:
                continue
            if message is None:
                raise RuntimeError("UnityCSharpLSP exited before responding")
            if "method" in message and "id" in message:
                self.respond(message["id"])
            if predicate(message):
                return message
        raise TimeoutError("timed out waiting for UnityCSharpLSP response")

    def request(self, method: str, params, timeout: float) -> dict:
        message_id = self.next_id
        self.next_id += 1
        self.send(method, params, message_id)
        return self.wait_for(lambda m: m.get("id") == message_id, timeout)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", default="UnityCSharpLSP.exe", help="server executable")
    parser.add_argument("--root", required=True, help="Unity project root")
    parser.add_argument("--command", required=True, help="unitycsharp.* command name")
    parser.add_argument("--args", default="{}", help="JSON object passed as arguments[0]")
    parser.add_argument("--timeout", type=float, default=120.0)
    options = parser.parse_args()

    try:
        arguments = json.loads(options.args)
    except json.JSONDecodeError as exc:
        print(f"invalid --args JSON: {exc}", file=sys.stderr)
        return 2
    if not isinstance(arguments, dict):
        print("--args must be a JSON object", file=sys.stderr)
        return 2

    client = LspClient(options.exe, options.root)
    try:
        client.request(
            "initialize",
            {
                "processId": None,
                "rootUri": client.root_uri,
                "capabilities": {},
                "workspaceFolders": [
                    {"uri": client.root_uri, "name": Path(options.root).name}
                ],
            },
            options.timeout,
        )
        client.send("initialized", {})
        response = client.request(
            "workspace/executeCommand",
            {"command": options.command, "arguments": [arguments]},
            options.timeout,
        )
    except (RuntimeError, TimeoutError) as exc:
        print(str(exc), file=sys.stderr)
        return 1
    finally:
        client.process.terminate()

    if "error" in response:
        print(json.dumps(response["error"], ensure_ascii=False, indent=2), file=sys.stderr)
        return 1
    print(json.dumps(response.get("result"), ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

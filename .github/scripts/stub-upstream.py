#!/usr/bin/env python3
"""A stub OpenAI-compatible provider for the NativeAOT smoke test.

Deliberately dependency-free: it uses only the Python standard library, so it runs
on any GitHub runner without a package install step, and it is small enough that a
reader can confirm at a glance that the smoke test is not cheating.

It emits each chunk with an explicit flush and a short delay, so that a gateway
which buffers the stream produces observably different output from one that does
not. The smoke test asserts on the arrival spacing, not just on the content.
"""

import json
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

CHUNKS = ["The ", "gate ", "is ", "open."]
CHUNK_DELAY_SECONDS = 0.15


class StubHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_POST(self):  # noqa: N802 - name mandated by BaseHTTPRequestHandler
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length) if length else b"{}"

        try:
            request = json.loads(body)
        except json.JSONDecodeError:
            request = {}

        model = request.get("model", "stub-model")

        if request.get("stream"):
            self._serve_stream(model)
        else:
            self._serve_buffered(model)

    def _serve_stream(self, model):
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        # Chunked rather than a declared length: the whole point is that the body
        # is produced incrementally.
        self.send_header("Transfer-Encoding", "chunked")
        self.end_headers()

        for index, text in enumerate(CHUNKS):
            time.sleep(CHUNK_DELAY_SECONDS)

            is_last = index == len(CHUNKS) - 1
            chunk = {
                "id": "chatcmpl-stub",
                "object": "chat.completion.chunk",
                "created": 1700000000,
                "model": model,
                "choices": [
                    {
                        "index": 0,
                        "delta": {
                            "role": "assistant" if index == 0 else None,
                            "content": text,
                        },
                        "finish_reason": "stop" if is_last else None,
                    }
                ],
            }

            if is_last:
                chunk["usage"] = {
                    "prompt_tokens": 11,
                    "completion_tokens": 7,
                    "total_tokens": 18,
                }

            self._write_chunked(f"data: {json.dumps(chunk)}\n\n")

        self._write_chunked("data: [DONE]\n\n")
        self._end_chunked()

    def _serve_buffered(self, model):
        payload = json.dumps(
            {
                "id": "chatcmpl-stub",
                "object": "chat.completion",
                "created": 1700000000,
                "model": model,
                "choices": [
                    {
                        "index": 0,
                        "message": {"role": "assistant", "content": "".join(CHUNKS)},
                        "finish_reason": "stop",
                    }
                ],
                "usage": {
                    "prompt_tokens": 11,
                    "completion_tokens": 7,
                    "total_tokens": 18,
                },
            }
        ).encode()

        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)
        self.wfile.flush()

    def _write_chunked(self, text):
        data = text.encode()
        self.wfile.write(f"{len(data):X}\r\n".encode())
        self.wfile.write(data)
        self.wfile.write(b"\r\n")
        # The flush is the reason this stub exists rather than a canned response.
        self.wfile.flush()

    def _end_chunked(self):
        self.wfile.write(b"0\r\n\r\n")
        self.wfile.flush()

    def log_message(self, format, *args):  # noqa: A002 - signature is fixed
        # Keep the CI log readable; failures are diagnosed from the gateway side.
        pass


def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 18081
    server = ThreadingHTTPServer(("127.0.0.1", port), StubHandler)
    print(f"stub upstream listening on 127.0.0.1:{port}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()

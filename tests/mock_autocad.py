"""Stand-in for the T3LabCAD add-in: speaks the same line-JSON protocol on one port.

    python -I tests/mock_autocad.py 49900

Tools: get_status (echo), fail (isError result). Used by tests/smoke_bridge.py.
"""

import json
import os
import socketserver
import sys

TOOLS = [
    {"name": "get_status", "description": "mock status", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "fail", "description": "always fails", "inputSchema": {"type": "object", "properties": {}}},
]


class Handler(socketserver.StreamRequestHandler):
    def handle(self):
        for line in self.rfile:
            try:
                req = json.loads(line)
            except ValueError:
                return
            method, params = req.get("method"), req.get("params") or {}
            if method == "health":
                result = {"app": "T3LabCAD", "version": "mock", "pid": os.getpid(), "port": self.server.server_address[1],
                          "autocad": "mock", "active_document": "mock.dwg", "calls": 0}
            elif method == "tools/list":
                result = {"tools": TOOLS}
            elif method == "tools/call":
                if params.get("name") == "fail":
                    result = {"content": [{"type": "text", "text": "mock failure"}], "isError": True}
                else:
                    text = json.dumps({"echo": params.get("arguments"), "big": "x" * int((params.get("arguments") or {}).get("big", 0))})
                    result = {"content": [{"type": "text", "text": text}]}
            else:
                self.wfile.write((json.dumps({"id": req.get("id"), "error": {"code": -32601, "message": "nope"}}) + "\n").encode())
                continue
            self.wfile.write((json.dumps({"id": req.get("id"), "result": result}) + "\n").encode())


if __name__ == "__main__":
    port = int(sys.argv[1])
    with socketserver.ThreadingTCPServer(("127.0.0.1", port), Handler) as server:
        server.daemon_threads = True
        print("mock on", port, flush=True)
        server.serve_forever()

"""Stand-in for the T3LabCAD add-in: speaks the same line-JSON protocol on one port.

    python -I tests/mock_autocad.py 49900

Tools: get_status (read, echo), update_entities (trusted, echo), run_command (experimental, echo),
fail (isError result), busy (the add-in's autocad_busy refusal). Used by tests/smoke_bridge.py.
"""

import json
import os
import socketserver
import sys

READ = {"readOnlyHint": True}
TOOLS = [
    {"name": "get_status", "description": "mock status", "inputSchema": {"type": "object", "properties": {}}, "annotations": READ},
    {"name": "update_entities", "description": "mock write", "inputSchema": {"type": "object", "properties": {}}, "annotations": {"readOnlyHint": False}},
    {"name": "run_command", "description": "mock command", "inputSchema": {"type": "object", "properties": {}}, "annotations": {"readOnlyHint": False}},
    {"name": "fail", "description": "always fails", "inputSchema": {"type": "object", "properties": {}}, "annotations": READ},
    {"name": "busy", "description": "always busy", "inputSchema": {"type": "object", "properties": {}}, "annotations": READ},
]
BUSY = {"error_code": "autocad_busy", "message": "AutoCAD is busy with another AI call (add_entities for ChatGPT, 31 s so far). Nothing was run.",
        "retryable": True, "nothing_ran": True, "retry_after_s": 5, "ahead": 0,
        "running": {"agent": "ChatGPT", "tool": "add_entities", "running_s": 31.0, "state": "running"}}


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
                          "autocad": "mock", "active_document": "mock.dwg", "calls": 0,
                          "queue": {"busy": False, "waiting": 0}}
            elif method == "tools/list":
                result = {"tools": TOOLS}
            elif method == "tools/call":
                if params.get("name") == "fail":
                    result = {"content": [{"type": "text", "text": "mock failure"}], "isError": True}
                elif params.get("name") == "busy":
                    result = {"content": [{"type": "text", "text": json.dumps(BUSY)}], "isError": True}
                else:
                    text = json.dumps({"echo": params.get("arguments"), "client": params.get("client"), "tool": params.get("name"),
                                       "lane": params.get("lane"), "queue_wait_s": params.get("queue_wait_s"),
                                       "big": "x" * int((params.get("arguments") or {}).get("big", 0))})
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

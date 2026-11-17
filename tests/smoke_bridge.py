"""Smoke test for bridge/t3lab_cad_bridge.py without AutoCAD.

    python -I tests/smoke_bridge.py

1. AutoCAD down: initialize/ping work, tools/list falls back, a call returns the "not reachable" error.
2. Mock add-in up: live tools/list, a forwarded call (pin stripped), isError passthrough, result capping,
   list_autocad_instances.
3. 3ds Max profile (T3LAB_BRIDGE_APP=T3LabMax): own server name and tools, ignores a T3LabCAD add-in
   on its ports, max_port pin stripped.
Uses ports 49900-49902 / 49910-49912 and a temporary APPDATA so the real cache is not touched.
"""

import json
import os
import subprocess
import sys
import tempfile
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BRIDGE = os.path.join(ROOT, "bridge", "t3lab_cad_bridge.py")
MOCK = os.path.join(ROOT, "tests", "mock_autocad.py")
PORT = 49901


class Client:
    def __init__(self, env):
        self.proc = subprocess.Popen([sys.executable, "-I", BRIDGE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.DEVNULL, env=env)
        self.next_id = 0

    def request(self, method, params=None):
        self.next_id += 1
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method}
        if params is not None:
            msg["params"] = params
        self.proc.stdin.write((json.dumps(msg) + "\n").encode())
        self.proc.stdin.flush()
        while True:
            reply = json.loads(self.proc.stdout.readline())
            if reply.get("id") == self.next_id:
                return reply

    def notify(self, method):
        self.proc.stdin.write((json.dumps({"jsonrpc": "2.0", "method": method}) + "\n").encode())
        self.proc.stdin.flush()

    def close(self):
        self.proc.stdin.close()
        self.proc.wait(10)


def check(label, condition, detail=""):
    print(("PASS " if condition else "FAIL ") + label + ("" if condition else "  -> " + str(detail)[:300]))
    if not condition:
        check.failed += 1


check.failed = 0


def main():
    appdata = tempfile.mkdtemp(prefix="t3labcad_test_")
    env = dict(os.environ, T3LABCAD_PORT_RANGE="49900-49902", APPDATA=appdata)

    # 1. no AutoCAD
    c = Client(env)
    r = c.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "0"}})
    check("initialize", r["result"]["serverInfo"]["name"] == "t3lab-autocad" and r["result"]["protocolVersion"] == "2025-06-18", r)
    c.notify("notifications/initialized")
    check("ping", c.request("ping")["result"] == {})
    tools = [t["name"] for t in c.request("tools/list")["result"]["tools"]]
    check("tools/list offline has local tools", "list_autocad_instances" in tools, tools)
    r = c.request("tools/call", {"name": "get_status", "arguments": {}})["result"]
    check("call offline -> not reachable", r.get("isError") and "not reachable" in r["content"][0]["text"], r)
    check("unknown method", "error" in c.request("nope/nope"))
    c.close()

    # 2. mock add-in
    mock = subprocess.Popen([sys.executable, "-I", MOCK, str(PORT)], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    mock.stdout.readline()
    try:
        c = Client(env)
        c.request("initialize", {"protocolVersion": "2099-01-01", "capabilities": {}})
        tools = [t["name"] for t in c.request("tools/list")["result"]["tools"]]
        check("tools/list live", "get_status" in tools and "switch_autocad_instance" in tools, tools)
        check("cache written", os.path.exists(os.path.join(appdata, "T3LabCAD", "tools_cache.json")))
        r = c.request("tools/call", {"name": "get_status", "arguments": {"a": 1, "autocad_port": PORT}})["result"]
        echo = json.loads(r["content"][0]["text"])["echo"]
        check("forwarded call, pin stripped", echo == {"a": 1}, echo)
        r = c.request("tools/call", {"name": "fail", "arguments": {}})["result"]
        check("isError passthrough", r.get("isError") is True, r)
        r = c.request("tools/call", {"name": "get_status", "arguments": {"big": 200000}})["result"]
        check("large result capped", "[truncated" in r["content"][0]["text"] and len(r["content"][0]["text"]) < 100000)
        r = c.request("tools/call", {"name": "list_autocad_instances", "arguments": {}})["result"]
        inst = json.loads(r["content"][0]["text"])["instances"]
        check("list_autocad_instances", len(inst) == 1 and inst[0]["port"] == PORT and inst[0]["current"], inst)
        c.close()
    finally:
        mock.terminate()

    # 3. 3ds Max profile
    env_max = dict(os.environ, T3LAB_BRIDGE_APP="T3LabMax", T3LABMAX_PORT_RANGE="49910-49912", APPDATA=appdata)
    mocks = [subprocess.Popen([sys.executable, "-I", MOCK, str(port), app], stdout=subprocess.PIPE,
                              stderr=subprocess.DEVNULL) for port, app in ((49910, "T3LabCAD"), (49911, "T3LabMax"))]
    for m in mocks:
        m.stdout.readline()
    try:
        c = Client(env_max)
        r = c.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}})
        check("max: initialize", r["result"]["serverInfo"]["name"] == "t3lab-3dsmax"
              and "3ds Max" in r["result"]["instructions"], r)
        tools = [t["name"] for t in c.request("tools/list")["result"]["tools"]]
        check("max: local tools", "list_3dsmax_instances" in tools and "list_autocad_instances" not in tools, tools)
        r = c.request("tools/call", {"name": "list_3dsmax_instances", "arguments": {}})["result"]
        inst = json.loads(r["content"][0]["text"])["instances"]
        check("max: only T3LabMax instances", [i["port"] for i in inst] == [49911], inst)
        r = c.request("tools/call", {"name": "get_status", "arguments": {"a": 2, "max_port": 49911}})["result"]
        check("max: pin stripped", json.loads(r["content"][0]["text"])["echo"] == {"a": 2}, r)
        r = c.request("tools/call", {"name": "get_status", "arguments": {"max_port": 49910}})["result"]
        check("max: pinned to a CAD add-in -> not run", r.get("isError") is True, r)
        check("max: cache in T3LabMax", os.path.exists(os.path.join(appdata, "T3LabMax", "tools_cache.json")))
        c.close()
    finally:
        for m in mocks:
            m.terminate()

    print("\n{} failure(s)".format(check.failed))
    sys.exit(1 if check.failed else 0)


if __name__ == "__main__":
    main()

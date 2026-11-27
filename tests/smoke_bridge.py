"""Smoke test for bridge/t3lab_cad_bridge.py without AutoCAD.

    python -I tests/smoke_bridge.py

1. AutoCAD down: initialize/ping work, tools/list falls back, a call returns the "not reachable" error.
2. Mock add-in up: live tools/list, a forwarded call (pin stripped, lane + queue deadline + session stamped),
   isError passthrough, autocad_busy passthrough, result capping, list_autocad_instances, the audit log.
4. The command line (bridge.py tools / help / call / cursors / status / verify): typed flags, --document, exit codes.
3. --tier=read --deny --agent: tools above the tier are hidden and refused before AutoCAD (tier_denied / tool_denied).
Uses ports 49900-49902 and a temporary APPDATA so the real cache is not touched.
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
    def __init__(self, env, *args):
        self.proc = subprocess.Popen([sys.executable, "-I", BRIDGE] + list(args), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
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
    with open(os.path.join(ROOT, "bridge", "tools_snapshot.json"), encoding="utf-8") as f:
        snapshot = [t["name"] for t in json.load(f)["tools"]]
    check("tools/list offline = shipped snapshot ({} tools)".format(len(snapshot)),
          all(n in tools for n in snapshot) and len(set(snapshot)) == len(snapshot), tools)
    r = c.request("tools/call", {"name": "get_status", "arguments": {}})["result"]
    check("call offline -> not reachable", r.get("isError") and "not reachable" in r["content"][0]["text"], r)
    check("unknown method", "error" in c.request("nope/nope"))
    c.close()

    # 2. mock add-in
    mock = subprocess.Popen([sys.executable, "-I", MOCK, str(PORT)], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    mock.stdout.readline()
    try:
        c = Client(env)
        c.request("initialize", {"protocolVersion": "2099-01-01", "capabilities": {}, "clientInfo": {"name": "Claude Code", "version": "2.1"}})
        tools = [t["name"] for t in c.request("tools/list")["result"]["tools"]]
        check("tools/list live", "get_status" in tools and "switch_autocad_instance" in tools, tools)
        check("cache written", os.path.exists(os.path.join(appdata, "T3LabCAD", "tools_cache.json")))
        r = c.request("tools/call", {"name": "get_status", "arguments": {"a": 1, "autocad_port": PORT}})["result"]
        reply = json.loads(r["content"][0]["text"])
        echo = reply["echo"]
        check("forwarded call, pin stripped", echo == {"a": 1}, echo)
        client = reply["client"] or {}
        check("clientInfo + session forwarded for Agent Cursors",
              client.get("name") == "Claude Code" and client.get("version") == "2.1" and client.get("session", "").isdigit(), client)
        check("lane + queue deadline stamped", reply["lane"] == "person" and reply["queue_wait_s"] == 45.0, reply)
        r = c.request("tools/call", {"name": "fail", "arguments": {}})["result"]
        check("isError passthrough", r.get("isError") is True, r)
        r = c.request("tools/call", {"name": "busy", "arguments": {}})["result"]
        body = json.loads(r["content"][0]["text"])
        check("autocad_busy passthrough", r.get("isError") and body["error_code"] == "autocad_busy" and body["nothing_ran"], r)
        check("experimental tool listed by default", "run_command" in tools, tools)
        r = c.request("tools/call", {"name": "get_status", "arguments": {"big": 200000}})["result"]
        check("large result capped", "[truncated" in r["content"][0]["text"] and len(r["content"][0]["text"]) < 100000)
        r = c.request("tools/call", {"name": "list_autocad_instances", "arguments": {}})["result"]
        inst = json.loads(r["content"][0]["text"])["instances"]
        check("list_autocad_instances", len(inst) == 1 and inst[0]["port"] == PORT and inst[0]["current"], inst)
        c.close()
        audit_dir = os.path.join(appdata, "T3LabCAD", "audit")
        lines = []
        for name in os.listdir(audit_dir) if os.path.isdir(audit_dir) else []:
            with open(os.path.join(audit_dir, name), encoding="utf-8") as f:
                lines += [json.loads(x) for x in f if x.strip()]
        codes = {(x["tool"], x["error_code"]) for x in lines}
        check("audit log: one line per add-in call, error codes, no arguments",
              ("get_status", None) in codes and ("busy", "autocad_busy") in codes and ("fail", "error") in codes
              and all("arguments" not in x for x in lines) and all(x["tool"] != "list_autocad_instances" for x in lines), lines)

        # 3. tier read + deny + agent name
        c = Client(env, "--tier=read", "--deny=fail", "--agent=Night bot", "--lane=auto", "--queue-wait=12", "--audit=0")
        c.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "openclaw", "version": "1"}})
        tools = [t["name"] for t in c.request("tools/list")["result"]["tools"]]
        check("tier read hides trusted + experimental + denied",
              "get_status" in tools and "update_entities" not in tools and "run_command" not in tools and "fail" not in tools, tools)
        for tool, code in (("update_entities", "tier_denied"), ("run_command", "tier_denied"), ("fail", "tool_denied")):
            r = c.request("tools/call", {"name": tool, "arguments": {}})["result"]
            body = json.loads(r["content"][0]["text"])
            check("{} refused before AutoCAD ({})".format(tool, code), r.get("isError") and body["error_code"] == code
                  and body["nothing_ran"] and body["retryable"] is False, body)
        r = c.request("tools/call", {"name": "get_status", "arguments": {}})["result"]
        reply = json.loads(r["content"][0]["text"])
        check("--agent / --lane / --queue-wait forwarded", reply["client"]["name"] == "Night bot" and reply["lane"] == "auto"
              and reply["queue_wait_s"] == 12.0, reply)
        c.close()

        # 4. the command line: same tools, typed flags, the document pin, exit codes
        def cli(*args):
            r = subprocess.run([sys.executable, "-I", BRIDGE] + list(args), capture_output=True, text=True, env=env, timeout=60)
            return r.returncode, r.stdout

        code, out = cli("tools", "values")
        check("cli tools: match first, * = takes the document", code == 0 and out.splitlines()[0].startswith("set_values") and "*" in out.splitlines()[0], out)
        code, out = cli("help", "set_values")
        check("cli help: typed parameters, required, choices, --document", code == 0 and "--element_ids" in out and "(required)" in out
              and "one of a|b" in out and "--document=" in out, out)
        code, out = cli("call", "set_values", "--element-ids=1,2", "--value=x", "--dry-run", "--mode=b", "--document=Other", "--out=compact")
        reply = json.loads(out.strip().splitlines()[-1]) if out.strip() else {}
        echo = reply.get("echo") or {}
        check("cli call: typed flags + pin forwarded, client cli", code == 0 and echo.get("element_ids") == [1, 2] and echo.get("dry_run") is True
              and echo.get("mode") == "b" and echo.get("autocad_document") == "Other" and (reply.get("client") or {}).get("name") == "cli", out)
        code, out = cli("call", "set_values", "--value=x")
        check("cli call: missing required -> exit 2, nothing ran", code == 2 and "element_ids" in out and "bad_arguments" in out, (code, out))
        code, out = cli("call", "set_values", "--elemnt_ids=1")
        check("cli call: misspelled -> did you mean", code == 2 and "Did you mean --element_ids" in out, (code, out))
        code, out = cli("call", "set_values", "--element_ids=1", "--mode=z")
        check("cli call: not a choice -> exit 2", code == 2 and "not one of" in out, (code, out))
        code, out = cli("call", "list_rows", "--out=tsv", "--pick=id,name")
        check("cli tsv: header once, one row per item, picked columns", code == 0 and out.strip().splitlines() == ["id\tname", "1\tL1", "2\tL2"], out)
        code, out = cli("call", "busy")
        check("cli call: tool refusal -> exit 1 with error_code", code == 1 and "error_code" in out, (code, out))
        code, out = cli("cursors")
        check("cli cursors -> agent_cursors status", code == 0 and json.loads(out)["echo"] == {"action": "status"}, out)
        code, out = cli("cursors", "off")
        check("cli cursors off is the user's -> exit 2", code == 2, (code, out))
        code, out = cli("status")
        check("cli status: instance + options", code == 0 and json.loads(out)["instances"][0]["port"] == PORT, out)
        code, out = cli("frobnicate")
        check("cli unknown command -> help, exit 2", code == 2 and "Commands" in out, (code, out))
        folder = tempfile.mkdtemp(prefix="t3lab_verify_")
        with open(os.path.join(folder, "a.pdf"), "wb") as f:
            f.write(b"%PDF-1.7 ...")
        with open(os.path.join(folder, "b.dwg"), "wb") as f:
            f.write(b"AC1032....")
        code, out = cli("verify", folder, "--expect=2")
        check("cli verify: good exports -> exit 0", code == 0 and json.loads(out)["ok"] == 2, out)
        with open(os.path.join(folder, "c.pdf"), "wb") as f:
            f.write(b"")
        with open(os.path.join(folder, "d.dwg"), "wb") as f:
            f.write(b"not a drawing")
        code, out = cli("verify", folder, "--ext=pdf,dwg")
        problems = {os.path.basename(x["path"]): x["problem"] for x in json.loads(out)["failed"]}
        check("cli verify: empty and wrong header found -> exit 1", code == 1 and problems.get("c.pdf") == "empty"
              and "does not start like" in problems.get("d.dwg", ""), out)
    finally:
        mock.terminate()

    print("\n{} failure(s)".format(check.failed))
    sys.exit(1 if check.failed else 0)


if __name__ == "__main__":
    main()

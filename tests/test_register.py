"""register_mcp.py against a fake home: register, idempotent re-run, check, remove; other entries kept.

    python -I tests/test_register.py
"""
import json
import os
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCRIPT = os.path.join(ROOT, "scripts", "register_mcp.py")
failed = 0


def check(label, ok, detail=""):
    global failed
    print(("PASS " if ok else "FAIL ") + label + ("" if ok else "  -> " + str(detail)[:300]))
    failed += 0 if ok else 1


def run(env, *args):
    r = subprocess.run([sys.executable, "-I", SCRIPT] + list(args), capture_output=True, text=True, env=env)
    last = r.stdout.strip().splitlines()[-1] if r.stdout.strip() else "{}"
    return r.returncode, json.loads(last)


def main():
    t = tempfile.mkdtemp(prefix="t3labcad_reg_")
    home, appdata = os.path.join(t, "home"), os.path.join(t, "appdata")
    for d in (os.path.join(home, ".cursor"), os.path.join(home, ".codex"), os.path.join(appdata, "Claude"), os.path.join(appdata, "Code", "User")):
        os.makedirs(d)
    with open(os.path.join(home, ".codex", "config.toml"), "w") as f:
        f.write('[model]\nname = "x"\n\n[mcp_servers.other]\ncommand = "a"\n')
    with open(os.path.join(appdata, "Claude", "claude_desktop_config.json"), "w") as f:
        json.dump({"mcpServers": {"t3lab-revit": {"command": "py"}}, "theme": "dark"}, f)
    # no claude CLI on PATH, so Claude Code is skipped (not found) in "all" mode
    env = {"HOME": home, "USERPROFILE": home, "APPDATA": appdata, "LOCALAPPDATA": os.path.join(t, "local"), "PATH": os.path.dirname(sys.executable)}
    args = ["--bridge", "C:\\Users\\u\\AppData\\Roaming\\T3LabCAD\\bridge.py", "--python", "C:\\Py\\python.exe"]

    code, out = run(env, *args)
    got = {r["client"]: r["status"] for r in out["results"]}
    check("register: exit 0", code == 0, out)
    check("register: 4 clients", got == {"claude-desktop": "registered", "codex": "registered", "cursor": "registered", "vscode": "registered"}, got)
    claude = json.load(open(os.path.join(appdata, "Claude", "claude_desktop_config.json")))
    check("claude: other server + settings kept", "t3lab-revit" in claude["mcpServers"] and claude["theme"] == "dark", claude)
    check("claude: entry", claude["mcpServers"]["t3lab-autocad"] == {"command": "C:\\Py\\python.exe", "args": ["-I", "C:/Users/u/AppData/Roaming/T3LabCAD/bridge.py"]}, claude)
    vs = json.load(open(os.path.join(appdata, "Code", "User", "mcp.json")))
    check("vscode: servers + type stdio", vs["servers"]["t3lab-autocad"]["type"] == "stdio", vs)
    toml = open(os.path.join(home, ".codex", "config.toml")).read()
    check("codex: table added, others kept", "[mcp_servers.t3lab-autocad]" in toml and "[mcp_servers.other]" in toml and 'name = "x"' in toml, toml)
    check("backups written", any(n.startswith("config.toml.bak-") for n in os.listdir(os.path.join(home, ".codex"))))

    code, out = run(env, *args)
    check("re-run: all unchanged", all(r["status"] == "unchanged" for r in out["results"]), out)
    code, out = run(env, "--check")
    check("check: all registered", len(out["results"]) == 4 and all(r["status"] == "registered" for r in out["results"]), out)
    code, out = run(env, "--remove")
    check("remove: all removed", all(r["status"] == "removed" for r in out["results"]), out)
    toml = open(os.path.join(home, ".codex", "config.toml")).read()
    check("codex: only ours removed", "t3lab-autocad" not in toml and "[mcp_servers.other]" in toml, toml)
    claude = json.load(open(os.path.join(appdata, "Claude", "claude_desktop_config.json")))
    check("claude: only ours removed", "t3lab-autocad" not in claude["mcpServers"] and "t3lab-revit" in claude["mcpServers"], claude)

    with open(os.path.join(home, ".cursor", "mcp.json"), "w") as f:
        f.write('{ // a comment\n "mcpServers": {} }')
    code, out = run(env, "--clients", "cursor", *args)
    check("JSON with comments is not overwritten", out["results"][0]["status"] == "fail" and "comment" in open(os.path.join(home, ".cursor", "mcp.json")).read(), out)

    print("\n{} failure(s)".format(failed))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()

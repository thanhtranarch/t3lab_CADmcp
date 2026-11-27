"""Add (or update / remove / check) the t3lab-autocad server in every MCP client found on this PC.

    python -I register_mcp.py --bridge <bridge.py> --python <python.exe> [--clients all|a,b] [--check] [--remove] [--dry-run] [--force]

Clients: claude-desktop, claude-code, codex, cursor, vscode, copilot, gemini, antigravity, windsurf.
"all" (default) = every client whose config folder or CLI exists; a client named explicitly is written even if not found.
Each config file is backed up (<file>.bak-<time>) before the first change; other servers and settings are kept.
Claude Desktop rewrites its config when it quits, so it is skipped while it runs (exit 3) unless --force.
--check prints what is registered where and changes nothing.

The last stdout line is one JSON object (for installers and AI agents): {"results": [{client, status, path, detail}]}.
Standard library only, Python 3.8+.
"""

import json
import os
import re
import shutil
import subprocess
import sys
import time

NAME = "t3lab-autocad"
CLAUDE_RUNNING = 3
HOME = os.path.expanduser("~")
APPDATA = os.environ.get("APPDATA") or os.path.join(HOME, "AppData", "Roaming")
LOCALAPPDATA = os.environ.get("LOCALAPPDATA") or os.path.join(HOME, "AppData", "Local")


# --------------------------------------------------------------------------- where each client keeps its config

def claude_desktop_paths():
    paths = [os.path.join(APPDATA, "Claude", "claude_desktop_config.json")]
    packages = os.path.join(LOCALAPPDATA, "Packages")
    try:
        for name in os.listdir(packages):     # Microsoft Store (MSIX) build keeps a private copy of %APPDATA%
            if name.lower().startswith("claude_") or name.lower().startswith("anthropic"):
                paths.append(os.path.join(packages, name, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"))
    except OSError:
        pass
    return paths


def claude_desktop_found():
    return any(os.path.isdir(os.path.dirname(p)) for p in claude_desktop_paths()) or \
        os.path.isdir(os.path.join(LOCALAPPDATA, "AnthropicClaude"))


def codex_path():
    home = os.environ.get("CODEX_HOME") or os.path.join(HOME, ".codex")
    return os.path.join(home, "config.toml")


def antigravity_path():
    candidates = [
        os.path.join(HOME, ".gemini", "antigravity-ide", "mcp_config.json"),
        os.path.join(HOME, ".gemini", "antigravity", "mcp_config.json"),
        os.path.join(HOME, ".antigravity", "mcp_config.json"),
    ]
    for c in candidates:
        if os.path.isfile(c):
            return c
    return candidates[0]


def antigravity_found():
    return any(os.path.isdir(os.path.dirname(p)) for p in (
        os.path.join(HOME, ".gemini", "antigravity-ide", "x"), os.path.join(HOME, ".gemini", "antigravity", "x"),
        os.path.join(HOME, ".antigravity", "x"))) or os.path.isdir(os.path.join(LOCALAPPDATA, "Programs", "Antigravity"))


def vscode_paths():
    out = []
    for edition in ("Code", "Code - Insiders"):
        user = os.path.join(APPDATA, edition, "User")
        if os.path.isdir(user):
            out.append(os.path.join(user, "mcp.json"))
    return out


def which(name):
    return shutil.which(name)


# JSON clients: (key, label, paths(), found(), top-level key, extra fields)
JSON_CLIENTS = [
    ("claude-desktop", "Claude Desktop", claude_desktop_paths, claude_desktop_found, "mcpServers", {}),
    ("cursor", "Cursor", lambda: [os.path.join(HOME, ".cursor", "mcp.json")],
     lambda: os.path.isdir(os.path.join(HOME, ".cursor")), "mcpServers", {}),
    ("vscode", "VS Code (Copilot)", vscode_paths, lambda: bool(vscode_paths()), "servers", {"type": "stdio"}),
    ("copilot", "GitHub Copilot CLI", lambda: [os.path.join(os.environ.get("COPILOT_HOME") or os.path.join(HOME, ".copilot"), "mcp-config.json")],
     lambda: os.path.isdir(os.environ.get("COPILOT_HOME") or os.path.join(HOME, ".copilot")), "mcpServers", {"type": "stdio", "tools": ["*"]}),
    ("gemini", "Gemini CLI", lambda: [os.path.join(HOME, ".gemini", "settings.json")],
     lambda: os.path.isfile(os.path.join(HOME, ".gemini", "settings.json")) or bool(which("gemini")), "mcpServers", {}),
    ("antigravity", "Antigravity", lambda: [antigravity_path()], antigravity_found, "mcpServers", {}),
    ("windsurf", "Windsurf", lambda: [os.path.join(HOME, ".codeium", "windsurf", "mcp_config.json")],
     lambda: os.path.isdir(os.path.join(HOME, ".codeium", "windsurf")), "mcpServers", {}),
]
ALL = ["claude-desktop", "claude-code", "codex"] + [c[0] for c in JSON_CLIENTS if c[0] != "claude-desktop"]


# --------------------------------------------------------------------------- helpers

def read_text(path):
    with open(path, "r", encoding="utf-8-sig") as f:      # Notepad / PowerShell 5 write a BOM
        return f.read()


def write_text(path, text, backed_up):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    if os.path.isfile(path) and path not in backed_up:
        backup = path + ".bak-" + time.strftime("%Y%m%d-%H%M%S")
        shutil.copy2(path, backup)
        backed_up.add(path)
    tmp = path + ".t3lab.tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    os.replace(tmp, path)


def claude_desktop_running():
    if os.name != "nt":
        return False
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-Process claude -ErrorAction SilentlyContinue).Path"],
                             capture_output=True, text=True, timeout=20, creationflags=0x08000000).stdout
    except (OSError, subprocess.SubprocessError):
        return False
    # Claude Code also ships a claude.exe: only the desktop app's install folders count
    return any(("\\anthropicclaude\\" in line.lower() or "\\windowsapps\\" in line.lower()) for line in out.splitlines())


def entry(python, bridge, extra):
    e = dict(extra)
    e["command"] = python
    e["args"] = ["-I", bridge]
    return e


def same(old, new):
    return isinstance(old, dict) and old.get("command") == new["command"] and old.get("args") == new["args"]


# --------------------------------------------------------------------------- one client

def do_json(key, label, path, top, new, mode, dry, backed_up):
    data = {}
    if os.path.isfile(path):
        raw = read_text(path).strip()
        if raw:
            try:
                data = json.loads(raw)
            except ValueError:
                return "fail", "not plain JSON (comments?): add the entry by hand, see README"
    if not isinstance(data, dict):
        return "fail", "unexpected JSON shape"
    servers = data.get(top)
    if mode == "check":
        cur = servers.get(NAME) if isinstance(servers, dict) else None
        return ("registered", json.dumps(cur)) if cur else ("not_registered", "")
    if mode == "remove":
        if not isinstance(servers, dict) or NAME not in servers:
            return "unchanged", "not registered"
        if dry:
            return "would_remove", ""
        del servers[NAME]
        write_text(path, json.dumps(data, ensure_ascii=False, indent=2) + "\n", backed_up)
        return "removed", ""
    if not isinstance(servers, dict):
        servers = data[top] = {}
    if same(servers.get(NAME), new):
        return "unchanged", "already registered"
    if dry:
        return "would_register", json.dumps(new)
    servers[NAME] = new
    write_text(path, json.dumps(data, ensure_ascii=False, indent=2) + "\n", backed_up)
    return "registered", ""


def toml_quote(v):
    return v.replace("\\", "\\\\").replace('"', '\\"')


SECTION = re.compile(r"^\s*\[\s*mcp_servers\.(?:\"" + re.escape(NAME) + r"\"|" + re.escape(NAME) + r")(?:\.[^\]]*)?\s*\]\s*$")
HEADER = re.compile(r"^\s*\[")


def toml_strip(text):
    out, skip = [], False
    for line in text.splitlines():
        if SECTION.match(line):
            skip = True
            continue
        if skip and HEADER.match(line):
            skip = False
        if not skip:
            out.append(line)
    while out and not out[-1].strip():
        out.pop()
    return "\n".join(out)


def do_codex(path, python, bridge, mode, dry, backed_up):
    text = read_text(path) if os.path.isfile(path) else ""
    has = any(SECTION.match(l) for l in text.splitlines())
    if mode == "check":
        return ("registered", "") if has else ("not_registered", "")
    if mode == "remove":
        if not has:
            return "unchanged", "not registered"
        if dry:
            return "would_remove", ""
        write_text(path, toml_strip(text) + "\n", backed_up)
        return "removed", ""
    block = ("[mcp_servers.{}]\ncommand = \"{}\"\nargs = [\"-I\", \"{}\"]\nstartup_timeout_sec = 60\n"
             .format(NAME, toml_quote(python), toml_quote(bridge)))
    base = toml_strip(text)
    new_text = (base + "\n\n" if base.strip() else "") + block
    if new_text.strip() == text.strip():
        return "unchanged", "already registered"
    if dry:
        return "would_register", block
    write_text(path, new_text, backed_up)
    return "registered", ""


def do_claude_code(python, bridge, mode, dry, backed_up):
    exe = which("claude")
    cfg = os.path.join(HOME, ".claude.json")
    if exe and mode != "check":
        if dry:
            return ("would_remove" if mode == "remove" else "would_register"), exe
        subprocess.run([exe, "mcp", "remove", "--scope", "user", NAME], capture_output=True, text=True, timeout=60)
        if mode == "remove":
            return "removed", ""
        r = subprocess.run([exe, "mcp", "add", "--scope", "user", NAME, "--", python, "-I", bridge],
                           capture_output=True, text=True, timeout=60)
        if r.returncode == 0:
            return "registered", "claude mcp add --scope user"
        return "fail", (r.stderr or r.stdout).strip()[:300]
    # no CLI on PATH: user-scope servers live in ~/.claude.json "mcpServers"
    if not os.path.isfile(cfg):
        return "not_found", "Claude Code not installed (no claude CLI, no ~/.claude.json)"
    return do_json("claude-code", "Claude Code", cfg, "mcpServers", entry(python, bridge, {"type": "stdio"}), mode, dry, backed_up)


# --------------------------------------------------------------------------- main

def parse(argv):
    opts = {"clients": "all", "mode": "register", "dry": False, "force": False, "bridge": None, "python": None}
    i = 0
    while i < len(argv):
        a = argv[i]
        if a in ("--bridge", "--python", "--clients"):
            opts[a[2:]] = argv[i + 1]
            i += 2
            continue
        if a == "--check":
            opts["mode"] = "check"
        elif a == "--remove":
            opts["mode"] = "remove"
        elif a == "--dry-run":
            opts["dry"] = True
        elif a == "--force":
            opts["force"] = True
        elif not a.startswith("--") and opts["bridge"] is None:
            opts["bridge"] = a                       # positional, like the AutoCAD script: <bridge> <python>
        elif not a.startswith("--") and opts["python"] is None:
            opts["python"] = a
        i += 1
    return opts


def main(argv):
    o = parse(argv)
    mode = o["mode"]
    if mode != "check" and (not o["bridge"] or not o["python"]) and mode != "remove":
        print(__doc__)
        return 2
    bridge = (o["bridge"] or "").replace("\\", "/")
    python = o["python"] or ""
    explicit = o["clients"] != "all"
    wanted = ALL if not explicit else [c.strip().lower() for c in o["clients"].split(",") if c.strip()]
    results = []
    backed_up = set()
    exit_code = 0

    def report(key, label, status, path="", detail=""):
        results.append({"client": key, "label": label, "status": status, "path": path, "detail": detail})
        mark = {"registered": "OK  ", "unchanged": "OK  ", "removed": "OK  ", "not_registered": "--  ",
                "not_found": "--  ", "skipped": "SKIP", "fail": "FAIL"}.get(status, "..  ")
        print("{} {:<20} {}{}".format(mark, label, status.replace("_", " "), (" - " + detail) if detail else "") +
              ("" if not path else "\n       " + path))

    for key in wanted:
        if key == "claude-code":
            status, detail = do_claude_code(python, bridge, mode, o["dry"], backed_up)
            if status == "not_found" and not explicit:
                continue
            report(key, "Claude Code", status, os.path.join(HOME, ".claude.json") if status != "not_found" else "", detail)
            continue
        if key == "codex":
            path = codex_path()
            if not explicit and not (os.path.isdir(os.path.dirname(path)) or which("codex")):
                continue
            status, detail = do_codex(path, python, bridge, mode, o["dry"], backed_up)
            report(key, "Codex (ChatGPT)", status, path, detail)
            continue
        spec = next((c for c in JSON_CLIENTS if c[0] == key), None)
        if spec is None:
            report(key, key, "fail", "", "unknown client (known: " + ", ".join(ALL) + ")")
            exit_code = 2
            continue
        _, label, paths, found, top, extra = spec
        if not explicit and not found():
            continue
        targets = paths()
        if key == "claude-desktop":
            targets = [p for p in targets if os.path.isdir(os.path.dirname(p))] or targets[:1]
            if mode != "check" and not o["dry"] and claude_desktop_running() and not o["force"]:
                report(key, label, "skipped", targets[0], "Claude Desktop is running and would overwrite the change when it quits: "
                                                           "quit it (tray icon > Quit), then run this again")
                exit_code = CLAUDE_RUNNING
                continue
        for path in targets or []:
            status, detail = do_json(key, label, path, top, entry(python, bridge, extra), mode, o["dry"], backed_up)
            report(key, label, status, path, detail)
            if status == "fail" and exit_code == 0:
                exit_code = 1
    if not results:
        print("No MCP client found. Install Claude Desktop (claude.ai/download) or another MCP app, then run this again.")
    print(json.dumps({"server": NAME, "mode": mode, "results": results}, ensure_ascii=False))
    return exit_code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

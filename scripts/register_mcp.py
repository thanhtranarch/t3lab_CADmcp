"""Add (or update) the t3lab-autocad server in Claude Desktop's config.

    python -I scripts/register_mcp.py <bridge.py path> <python.exe> [--dry-run]

Reuses the interpreter of an existing t3lab-revit entry so both bridges run the
same Python. Backs up the config before writing; no change if already correct.

Claude Desktop keeps this file in memory and writes it back when it quits, so an
entry added while it runs is lost: refuses (exit 3) while Claude.exe is running
unless --force.
"""

import json
import os
import shutil
import subprocess
import sys
import time

NAME = "t3lab-autocad"
CLAUDE_RUNNING = 3


def claude_desktop_running():
    try:
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq claude.exe", "/NH"],
                             capture_output=True, text=True, timeout=15).stdout
    except (OSError, subprocess.SubprocessError):
        return False
    return "claude.exe" in out.lower()


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    dry_run = "--dry-run" in sys.argv
    if len(args) != 2:
        print(__doc__)
        return 2
    bridge, python = args[0].replace("\\", "/"), args[1]

    path = os.path.join(os.environ["APPDATA"], "Claude", "claude_desktop_config.json")
    data = {}
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
    servers = data.setdefault("mcpServers", {})
    command = (servers.get("t3lab-revit") or {}).get("command") or python
    entry = {"command": command, "args": ["-I", bridge]}

    if servers.get(NAME) == entry:
        print("Claude Desktop: {} already registered".format(NAME))
        return 0
    if dry_run:
        print("Claude Desktop: would set {} = {}".format(NAME, json.dumps(entry)))
        return 0
    if claude_desktop_running() and "--force" not in sys.argv:
        print("Claude Desktop is running and rewrites its config when it quits, so the entry would be lost.")
        print("Quit it (tray icon > Quit), run this again, then start Claude Desktop.")
        return CLAUDE_RUNNING

    if os.path.exists(path):
        backup = path + ".bak-" + time.strftime("%Y%m%d-%H%M%S")
        shutil.copy2(path, backup)
        print("Claude Desktop: backup -> {}".format(backup))
    else:
        os.makedirs(os.path.dirname(path), exist_ok=True)
    servers[NAME] = entry
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    os.replace(tmp, path)
    print("Claude Desktop: registered {} ({} -I {})".format(NAME, command, bridge))
    print("Restart Claude Desktop to load it.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

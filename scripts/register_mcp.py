"""Add (or update) the t3lab-autocad server in Claude Desktop's config.

    python -I scripts/register_mcp.py <bridge.py path> <python.exe> [--dry-run]

Reuses the interpreter of an existing t3lab-revit entry so both bridges run the
same Python. Backs up the config before writing; no change if already correct.
"""

import json
import os
import shutil
import sys
import time

NAME = "t3lab-autocad"


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

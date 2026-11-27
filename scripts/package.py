"""Assemble the T3LabCAD release: dist/T3LabCAD.zip

    python scripts/package.py            # after building R24, R25 and R26 (see README)

Zip layout, read by install.ps1 and by T3Lab's MCP Control (Connect installs it):
    T3LabCAD.bundle/PackageContents.xml
    T3LabCAD.bundle/Contents/<R24|R25|R26>/T3LabCAD.Loader.dll, T3LabCAD.Core.dll
    T3LabCAD/bridge.py, T3LabCAD/tools_snapshot.json, T3LabCAD/register_mcp.py (Connect AI)
    manifest.json   {"name", "version", "series": {...}, "bundle", "bridge"}

Standard library only.
"""
import json
import os
import re
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SERIES = {
    "R24": "AutoCAD / Civil 3D 2022-2024 (.NET Framework 4.8)",
    "R25": "AutoCAD / Civil 3D 2025-2026 (.NET 8)",
    "R26": "AutoCAD / Civil 3D 2027 (.NET 10)",
}
FILES = ("T3LabCAD.Loader.dll", "T3LabCAD.Core.dll")


def version():
    with open(os.path.join(ROOT, "src", "Directory.Build.props"), encoding="utf-8") as f:
        return re.search(r"<Version>([^<]+)</Version>", f.read()).group(1)


def entries(configuration="Release"):
    """[(source path, path in the zip)]; raises when a series was not built."""
    out = [(os.path.join(ROOT, "bundle", "PackageContents.xml"), "T3LabCAD.bundle/PackageContents.xml")]
    missing = []
    for series in SERIES:
        for name in FILES:
            project = name[:-4]
            src = os.path.join(ROOT, "src", project, "bin", configuration, series, name)
            if not os.path.isfile(src):
                missing.append(src)
            out.append((src, "T3LabCAD.bundle/Contents/{}/{}".format(series, name)))
    if missing:
        raise SystemExit("Not built:\n  " + "\n  ".join(missing))
    out.append((os.path.join(ROOT, "bridge", "t3lab_cad_bridge.py"), "T3LabCAD/bridge.py"))
    out.append((os.path.join(ROOT, "scripts", "register_mcp.py"), "T3LabCAD/register_mcp.py"))   # the ribbon's Connect AI runs it
    snapshot = os.path.join(ROOT, "bridge", "tools_snapshot.json")
    if os.path.isfile(snapshot):
        out.append((snapshot, "T3LabCAD/tools_snapshot.json"))
    return out


def build(dest=None):
    dest = dest or os.path.join(ROOT, "dist", "T3LabCAD.zip")
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    manifest = {"name": "T3LabCAD", "version": version(), "series": SERIES,
                "bundle": "T3LabCAD.bundle", "bridge": "T3LabCAD/bridge.py"}
    with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED) as z:
        for src, arc in entries():
            z.write(src, arc)
        z.writestr("manifest.json", json.dumps(manifest, indent=2))
    return dest


if __name__ == "__main__":
    path = build(sys.argv[1] if len(sys.argv) > 1 else None)
    print("{} ({} bytes)".format(path, os.path.getsize(path)))

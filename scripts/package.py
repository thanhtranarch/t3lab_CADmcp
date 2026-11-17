"""Assemble the releases: dist/T3LabCAD.zip and dist/T3LabMax.zip

    python scripts/package.py            # after building R24, R25 and R26 (see README)
    python scripts/package.py --max      # T3LabMax only (pure Python, nothing to build)

Zip layout, read by install.ps1 and by T3Lab's MCP Control (Connect installs it):
    T3LabCAD.bundle/PackageContents.xml
    T3LabCAD.bundle/Contents/<R24|R25|R26>/T3LabCAD.Loader.dll, T3LabCAD.Core.dll
    T3LabCAD/bridge.py, T3LabCAD/tools_snapshot.json
    manifest.json   {"name", "version", "series": {...}, "bundle", "bridge"}
T3LabMax.zip: T3LabMax.bundle/PackageContents.xml, T3LabMax.bundle/Contents/scripts/*,
    T3LabMax/bridge.py (the same bridge; the folder picks the 3ds Max profile),
    T3LabMax/tools_snapshot.json (from the server's own tool list), manifest.json.

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


MAX_SRC = os.path.join(ROOT, "src", "T3LabMax")
MAX_SCRIPTS = ("t3lab_max_mcp.py", "t3lab_max_mcp_start.ms")


def max_tools():
    """MCP tool list of the 3ds Max server (importable without 3ds Max)."""
    sys.path.insert(0, MAX_SRC)
    try:
        import t3lab_max_mcp
    finally:
        sys.path.remove(MAX_SRC)
    return t3lab_max_mcp.VERSION, t3lab_max_mcp.tools_list()


def build_max(dest=None):
    dest = dest or os.path.join(ROOT, "dist", "T3LabMax.zip")
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    max_version, tools = max_tools()
    with open(os.path.join(ROOT, "bundle_max", "PackageContents.xml"), encoding="utf-8") as f:
        xml = f.read()
    if 'AppVersion="{}"'.format(max_version) not in xml:
        raise SystemExit("bundle_max/PackageContents.xml AppVersion != t3lab_max_mcp.VERSION " + max_version)
    manifest = {"name": "T3LabMax", "version": max_version, "series": {"2022-2027": "3ds Max 2022-2027 (pymxs)"},
                "bundle": "T3LabMax.bundle", "bridge": "T3LabMax/bridge.py"}
    with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("T3LabMax.bundle/PackageContents.xml", xml)
        for name in MAX_SCRIPTS:
            z.write(os.path.join(MAX_SRC, name), "T3LabMax.bundle/Contents/scripts/" + name)
        z.write(os.path.join(ROOT, "bridge", "t3lab_cad_bridge.py"), "T3LabMax/bridge.py")
        z.writestr("T3LabMax/tools_snapshot.json", json.dumps({"tools": tools}, indent=1))
        z.writestr("manifest.json", json.dumps(manifest, indent=2))
    return dest


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    paths = [build_max()] if "--max" in sys.argv else [build(args[0] if args else None), build_max()]
    for path in paths:
        print("{} ({} bytes)".format(path, os.path.getsize(path)))

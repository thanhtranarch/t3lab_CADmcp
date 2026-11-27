"""Every tool that may run in a document that is not the active one must not touch the UI side.

    python -I scripts/check_tool_scopes.py            # exit 1 when a declaration is wrong
    python -I scripts/check_tool_scopes.py --report   # what each tool touches

A tool is declared in Registry.Add with scope: ToolScope.Any (default: active, inactive and background documents),
ToolScope.Open (open drawings, not background ones) or
ToolScope.Active (the active document only). This script reads each handler and every helper it reaches (same-file
static methods, and the shared helper classes) and fails when a tool declared for more documents uses something that only
means the active one. Same script in T3Lab Revit MCP and T3Lab CAD MCP; only the patterns differ. Standard library only.
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gen_tools_snapshot as gen  # noqa: E402  (tokenizer + Registry.Add parser)

# pattern -> the scope it needs
NEEDS = [
    (r"\bc(?:tx)?\.Ed\b|\.Editor\b", "ToolScope.Active"),   # the editor: views, selection, prompts, zoom
    (r"\bMdiActiveDocument\b", "ToolScope.Active"),
    (r"\bSendStringToExecute\b|\bCommandInProgress\b", "ToolScope.Active"),
    (r"\bLayoutManager\.Current\b", "ToolScope.Active"),
    (r"\b[GS]etSystemVariable\b", "ToolScope.Active"),        # sysvars are the active drawing's
    (r"\bUpdateScreen\b|\bCapturePreviewImage\b", "ToolScope.Active"),
    (r"\bc(?:tx)?\.Doc\b", "ToolScope.Open"),                 # a Document: background drawings are databases only
]
HELPER_CLASSES = ["Acad", "Geo", "Describe", "Csv"]
RANK = {"ToolScope.Any": 0, "ToolScope.Open": 1, "ToolScope.Active": 2}
NAMES = {0: "Any", 1: "Open", 2: "Active"}


def methods(text):
    """name -> body of every method in a source file (brace matched; good enough for this code base)."""
    out = {}
    for m in re.finditer(r"\n\s+(?:internal |public |private )?static [\w<>\[\], ]+? (\w+)\s*\([^;{]*?\)\s*(?:where [^{]+)?\{", text):
        depth, i = 1, m.end()
        while depth and i < len(text):
            if text[i] == "{":
                depth += 1
            elif text[i] == "}":
                depth -= 1
            i += 1
        out.setdefault(m.group(1), "")
        out[m.group(1)] += text[m.end():i]
    return out


def strip(code):
    """Comments, char and string literals out (a '"' char literal must not open a string), so only code is matched."""
    code = re.sub(r"/\*.*?\*/", "", code, flags=re.S)
    code = re.sub(r"//[^\n]*", "", code)
    code = re.sub(r"'(?:[^'\\\n]|\\.)'", "' '", code)
    code = re.sub(r'@"(?:[^"]|"")*"', '""', code)
    return re.sub(r'\$?"(?:[^"\\\n]|\\.)*"', '""', code)


def main():
    files = gen.read_sources()
    by_file = {p: methods(strip(t)) for p, t in files.items()}
    helpers = {}
    for p, ms in by_file.items():
        for cls in HELPER_CLASSES:
            if re.search(r"\bstatic class " + cls + r"\b", files[p]):
                for name, body in ms.items():
                    helpers[cls + "." + name] = body

    def need_of(body, local, seen):
        level, why = 0, []
        for pattern, scope in NEEDS:
            if re.search(pattern, body):
                level = max(level, RANK[scope])
                why.append(pattern)
        for callee in set(re.findall(r"\b([A-Z]\w*)\s*\(", body)):
            if callee in local and callee not in seen:
                seen.add(callee)
                l, w = need_of(local[callee], local, seen)
                if l:
                    level, why = max(level, l), why + [callee + ">" + x for x in w]
        for cls, name in set(re.findall(r"\b(" + "|".join(HELPER_CLASSES) + r")\.(\w+)\s*\(", body)):
            key = cls + "." + name
            if key in helpers and key not in seen:
                seen.add(key)
                l, w = need_of(helpers[key], {}, seen)
                if l:
                    level, why = max(level, l), why + [key + ">" + x for x in w]
        return level, why

    names = gen.constants(files)
    registry = next(t for p, t in files.items() if os.path.basename(p) == "Registry.cs")
    order = re.findall(r"(\w+)\.Register\(\);", re.search(r"static void Build\(\)\s*\{(.*?)\}", registry, re.S).group(1))
    bad, report = [], []
    for cls in order:
        path = next(p for p, t in files.items() if re.search(r"\bclass\s+" + cls + r"\b", t))
        src = files[path]
        start = re.search(r"\bclass\s+" + cls + r"\b", src).start()
        nxt = re.search(r"\n    (?:internal|public)\s+(?:static\s+|sealed\s+)*(?:class|enum)\s", src[start + 1:])
        part = src[start:start + 1 + nxt.start()] if nxt else src[start:]
        local = methods(strip(part))
        for m in re.finditer(r"\bRegistry\.Add\(", part):
            args = gen.Parser(gen.statement_tokens(part, m.end(), depth=1), names).args()
            positional = [a for a in args if not isinstance(a, tuple)]
            named = dict(a for a in args if isinstance(a, tuple))
            tool, handler = positional[0], positional[5]
            if named.get("needsDocument", True) is False:
                continue          # works on its own arguments (documents by name), never pinned
            declared = RANK[named.get("scope", "ToolScope.Any")]
            level, why = need_of(local.get(handler, ""), local, {handler})
            report.append("{:28} declared {:6} needs {:6} {}".format(tool, NAMES[declared], NAMES[level], ", ".join(why[:3])))
            if level > declared:
                bad.append("{}: declared {} but uses {} (scope: ToolScope.{})".format(tool, NAMES[declared], ", ".join(why[:4]), NAMES[level]))
    if "--report" in sys.argv:
        print("\n".join(report))
    if bad:
        print("Tools that would run on a document their code cannot reach:\n  " + "\n  ".join(bad))
        sys.exit(1)
    print("tool scopes ok ({} tools that take a document)".format(len(report)))


if __name__ == "__main__":
    main()

"""Rebuild bridge/tools_snapshot.json from the Registry.Add(...) calls in src/T3LabCAD.Core, without AutoCAD.

    python scripts/gen_tools_snapshot.py           # write the snapshot
    python scripts/gen_tools_snapshot.py --check   # exit 1 when the snapshot is out of date (CI)

The snapshot is what the bridge lists while AutoCAD is not running. It must equal what the add-in
returns for tools/list, so this script evaluates the same expressions the C# code builds: string
literals joined with +, the Sch schema helpers (Obj, ObjR, P, Str, ...), const strings and
static readonly string[] fields. Tools are emitted in Registry.Build() order. Standard library only.
"""
import glob
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CORE = os.path.join(ROOT, "src", "T3LabCAD.Core")
SNAPSHOT = os.path.join(ROOT, "bridge", "tools_snapshot.json")


# --------------------------------------------------------------------------- C# tokens

TOKEN = re.compile(r"""
    (?P<ws>\s+|//[^\n]*|/\*.*?\*/)
  | (?P<vstr>@"(?:[^"]|"")*")
  | (?P<str>"(?:[^"\\\n]|\\.)*")
  | (?P<num>\d+(?:\.\d+)?)
  | (?P<id>[A-Za-z_][A-Za-z0-9_]*)
  | (?P<op>[(){}\[\],+.:;=])
""", re.S | re.X)

ESCAPES = {"n": "\n", "t": "\t", "r": "\r", "0": "\0", "\\": "\\", '"': '"', "'": "'"}


def unescape(body):
    out, i = [], 0
    while i < len(body):
        ch = body[i]
        if ch == "\\":
            nxt = body[i + 1]
            if nxt == "u":
                out.append(chr(int(body[i + 2:i + 6], 16)))
                i += 6
                continue
            out.append(ESCAPES[nxt])
            i += 2
            continue
        out.append(ch)
        i += 1
    return "".join(out)


def tokens(text, start=0):
    pos = start
    while pos < len(text):
        m = TOKEN.match(text, pos)
        if not m:
            raise SyntaxError("cannot tokenize at: " + text[pos:pos + 60])
        pos = m.end()
        kind = m.lastgroup
        if kind == "ws":
            continue
        value = m.group(kind)
        if kind == "str":
            yield "str", unescape(value[1:-1]), m.start()
        elif kind == "vstr":
            yield "str", value[2:-1].replace('""', '"'), m.start()
        else:
            yield kind, value, m.start()


# --------------------------------------------------------------------------- Sch mirror (Registry.cs)

def q(s):
    return "'" + s + "'"


def obj_r(required, *props):
    req = "" if not required else ",'required':[" + ",".join(q(r.strip()) for r in required.split(",")) + "]"
    return "{'type':'object','properties':{" + ",".join(props) + "}" + req + "}"


def enum(d, *values):
    return "{'type':'string','enum':[" + ",".join(q(v) for v in values) + "],'description':'" + d + "'}"


SCH = {
    "Obj": lambda *props: obj_r("", *props),
    "ObjR": obj_r,
    "P": lambda name, schema: q(name) + ":" + schema,
    "Str": lambda d: "{'type':'string','description':'" + d + "'}",
    "Num": lambda d: "{'type':'number','description':'" + d + "'}",
    "Int": lambda d: "{'type':'integer','description':'" + d + "'}",
    "Bool": lambda d: "{'type':'boolean','description':'" + d + "'}",
    "Any": lambda d: "{'description':'" + d + "'}",
    "Strs": lambda d: "{'type':'array','items':{'type':'string'},'description':'" + d + "'}",
    "Pt": lambda d: "{'type':'array','items':{'type':'number'},'minItems':2,'maxItems':3,'description':'" + d + "'}",
    "Pts": lambda d: "{'type':'array','items':{'type':'array','items':{'type':'number'}},'description':'" + d + "'}",
    "Enum": enum,
    "ObjAny": lambda d: "{'type':'object','description':'" + d + "'}",
    "Arr": lambda d, item: "{'type':'array','items':" + item + ",'description':'" + d + "'}",
    "EnumArr": lambda d, *values: "{'type':'array','items':{'type':'string','enum':[" + ",".join(q(v) for v in values) +
                                  "]},'description':'" + d + "'}",
}


# --------------------------------------------------------------------------- expression evaluation

class Parser:
    def __init__(self, toks, names):
        self.toks = toks
        self.i = 0
        self.names = names

    def peek(self, k=0):
        return self.toks[self.i + k] if self.i + k < len(self.toks) else (None, None, None)

    def take(self, value=None):
        tok = self.peek()
        if value is not None and tok[1] != value:
            raise SyntaxError("expected {!r}, got {!r}".format(value, tok[1]))
        self.i += 1
        return tok

    def expr(self):
        value = self.term()
        while self.peek()[1] == "+":
            self.take("+")
            right = self.term()
            value = str(value) + str(right)
        return value

    def term(self):
        kind, value, _ = self.take()
        if kind == "str":
            return value
        if kind == "num":
            return value
        if value == "{":  # array initializer
            items = []
            while self.peek()[1] != "}":
                items.append(self.expr())
                if self.peek()[1] == ",":
                    self.take(",")
            self.take("}")
            return items
        if kind == "id":
            if value in ("true", "false"):
                return value == "true"
            name = value
            while self.peek()[1] == ".":
                self.take(".")
                name += "." + self.take()[1]
            if self.peek()[1] == "(":
                self.take("(")
                args = self.args()
                short = name.split(".")[-1]
                fn = SCH.get(short)
                if fn is None:
                    raise SyntaxError("unknown call " + name)
                flat = []
                for a in args:
                    flat.extend(a if isinstance(a, list) else [a])
                return fn(*flat)
            short = name.split(".")[-1]
            if name.startswith("ToolKind."):
                return name
            if short in self.names:
                return self.names[short]
            return name  # handler method name
        raise SyntaxError("unexpected token {!r}".format(value))

    def args(self):
        """Arguments up to the closing ')': positional values, then named ones as ('name', value)."""
        out = []
        while self.peek()[1] != ")":
            if self.peek()[0] == "id" and self.peek(1)[1] == ":":
                key = self.take()[1]
                self.take(":")
                out.append((key, self.expr()))
            else:
                out.append(self.expr())
            if self.peek()[1] == ",":
                self.take(",")
        self.take(")")
        return out


def statement_tokens(text, start, depth=0):
    """Tokens from start to the end of the statement (the ';' at depth 0; depth=1 when start is inside a call)."""
    out = []
    for tok in tokens(text, start):
        if tok[1] in "({[":
            depth += 1
        elif tok[1] in ")}]":
            depth -= 1
        elif tok[1] == ";" and depth == 0:
            return out
        out.append(tok)
    raise SyntaxError("unterminated statement")


def read_sources():
    files = {}
    for path in glob.glob(os.path.join(CORE, "**", "*.cs"), recursive=True):
        if os.sep + "obj" + os.sep in path or os.sep + "bin" + os.sep in path:
            continue
        with open(path, encoding="utf-8-sig") as f:
            files[path] = f.read()
    return files


def constants(files):
    names = {}
    decl = re.compile(r"\b(?:const\s+string|static\s+readonly\s+string\[\])\s+([A-Za-z_]\w*)\s*=")
    for text in files.values():
        for m in decl.finditer(text):
            toks = statement_tokens(text, m.end())
            value = Parser(toks, names).expr()
            if m.group(1) in names and names[m.group(1)] != value:
                raise SystemExit("constant {} is defined twice with different values".format(m.group(1)))
            names[m.group(1)] = value
    return names


def tools():
    files = read_sources()
    names = constants(files)
    registry = next(t for p, t in files.items() if os.path.basename(p) == "Registry.cs")
    build = re.search(r"static void Build\(\)\s*\{(.*?)\}", registry, re.S).group(1)
    order = re.findall(r"(\w+)\.Register\(\);", build)

    result = []
    for cls in order:
        src = next((t for t in files.values() if re.search(r"\bclass\s+" + cls + r"\b", t)), None)
        if src is None:
            raise SystemExit("class {} not found".format(cls))
        for m in re.finditer(r"\bRegistry\.Add\(", src):
            toks = statement_tokens(src, m.end(), depth=1)
            parser = Parser(toks, names)
            args = parser.args()
            positional = [a for a in args if not isinstance(a, tuple)]
            named = dict(a for a in args if isinstance(a, tuple))
            name, title, kind, description, schema = positional[:5]
            result.append({
                "name": name,
                "title": title,
                "description": description,
                "inputSchema": json.loads(schema.replace("'", '"')),
                "annotations": {
                    "title": title,
                    "readOnlyHint": kind == "ToolKind.Read",
                    "destructiveHint": bool(named.get("destructive", False)),
                    "openWorldHint": False,
                },
            })
    seen = set()
    for t in result:
        if t["name"] in seen:
            raise SystemExit("duplicate tool name " + t["name"])
        seen.add(t["name"])
    return result


def main():
    generated = {"tools": tools()}
    if "--check" in sys.argv:
        with open(SNAPSHOT, encoding="utf-8") as f:
            current = json.load(f)
        if current != generated:
            old = {t["name"]: t for t in current.get("tools", [])}
            new = {t["name"]: t for t in generated["tools"]}
            changed = sorted(n for n in new if n in old and old[n] != new[n])
            print("tools_snapshot.json is out of date: run python scripts/gen_tools_snapshot.py")
            print("  added:", sorted(set(new) - set(old)) or "-")
            print("  removed:", sorted(set(old) - set(new)) or "-")
            print("  changed:", changed or "-")
            if [t["name"] for t in current.get("tools", [])] != [t["name"] for t in generated["tools"]] and not changed:
                print("  (order differs)")
            sys.exit(1)
        print("tools_snapshot.json is up to date ({} tools)".format(len(generated["tools"])))
        return
    with open(SNAPSHOT, "w", encoding="utf-8") as f:
        json.dump(generated, f, ensure_ascii=False, indent=1)
    print("wrote {} ({} tools)".format(os.path.relpath(SNAPSHOT, ROOT), len(generated["tools"])))


if __name__ == "__main__":
    main()

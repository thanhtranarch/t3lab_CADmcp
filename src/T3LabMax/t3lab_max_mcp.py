# -*- coding: utf-8 -*-
"""T3Lab 3ds Max MCP add-in: lets an AI app (through bridge/t3lab_cad_bridge.py,
deployed as %APPDATA%\\T3LabMax\\bridge.py) read and edit the open 3ds Max scene.

Loaded by T3LabMax.bundle (3ds Max 2022-2027) at start-up through
t3lab_max_mcp_start.ms. Same wire as T3LabCAD: TCP 127.0.0.1, first free port in
48910-48918, one JSON object per line each way:
    {"id": 1, "method": "health" | "tools/list" | "tools/call", "params": {...}}
    {"id": 1, "result": {...}}  or  {"id": 1, "error": {"code", "message"}}
health and tools/list are answered on the socket thread; tools/call runs on 3ds
Max's main thread (a Qt timer drains the queue), a change as one undo step.

Python 3.7+ (3ds Max 2022 ships 3.7): no walrus, no match. Test without 3ds Max:
python -I tests/test_max_server.py (fake pymxs).
"""
import base64
import json
import os
import queue
import socketserver
import tempfile
import threading
import traceback

VERSION = "1.1.0"
APP = "T3LabMax"
PORT_MIN, PORT_MAX = 48910, 48918
CALL_TIMEOUT = 600.0          # the bridge waits longer (900 s)
PUMP_MS = 25
MAX_ROWS = 2000

try:                          # inside 3ds Max
    import pymxs
    rt = pymxs.runtime
except ImportError:           # tests install a fake before importing this module
    pymxs = None
    rt = None


def _qt_core():
    try:
        from PySide6 import QtCore          # 3ds Max 2025+
    except ImportError:
        from PySide2 import QtCore          # 3ds Max 2022-2024
    return QtCore


# ─── helpers (main thread) ───────────────────────────────────────────────────

def _r(v):
    return round(float(v), 6)


def _p3(p):
    return [_r(p.x), _r(p.y), _r(p.z)]


def _s(v):
    return "" if v is None else str(v)


def _node_by_name(name):
    node = rt.getNodeByName(name)
    if node is None:
        raise ValueError("No object named '{}'.".format(name))
    return node


def _nodes(names):
    if not names:
        raise ValueError("names is empty.")
    return [_node_by_name(n) for n in names]


def _layer_name(node):
    try:
        return _s(node.layer.name)
    except Exception:
        return ""


def _bbox(node):
    try:
        lo, hi = rt.nodeGetBoundingBox(node, rt.matrix3(1))
        return [_p3(lo), _p3(hi)]
    except Exception:
        return None


def _brief(node):
    return {"name": _s(node.name), "class": _s(rt.classOf(node)),
            "superclass": _s(rt.superClassOf(node)), "layer": _layer_name(node),
            "position": _p3(node.position), "hidden": bool(node.isHidden)}


def _units():
    try:
        return _s(rt.units.SystemType)
    except Exception:
        return ""


def _document():
    try:
        return _s(rt.maxFilePath) + _s(rt.maxFileName)
    except Exception:
        return ""


def _color(rgb):
    if not isinstance(rgb, (list, tuple)) or len(rgb) != 3:
        raise ValueError("color must be [r, g, b] (0-255).")
    return rt.color(*[int(c) for c in rgb])


# ─── tools ───────────────────────────────────────────────────────────────────

TOOLS = []


def tool(name, description, properties=None, required=(), write=False, destructive=False):
    def wrap(fn):
        TOOLS.append({"name": name, "description": description, "write": write, "fn": fn,
                      "destructive": destructive,
                      "schema": {"type": "object", "properties": properties or {},
                                 "required": list(required)}})
        return fn
    return wrap


S_NAMES = {"type": "array", "items": {"type": "string"}, "description": "Object names"}
S_XYZ = {"type": "array", "items": {"type": "number"}, "description": "[x, y, z] in system units"}


@tool("get_status", "3ds Max version, open scene file, system units, object / selection / layer "
      "counts, current renderer. Start here.")
def get_status(args):
    return {"max_version": _s(rt.maxVersion()[0]) if rt.maxVersion() else "",
            "scene": _document() or "(unsaved scene)", "units": _units(),
            "objects": len(list(rt.objects)), "selected": len(list(rt.selection)),
            "layers": int(rt.LayerManager.count), "renderer": _s(rt.renderers.current),
            "addin": VERSION}


@tool("list_objects", "Objects in the scene with class, layer, position (filters combine). "
      "detail=count gives totals per class only.",
      {"name_contains": {"type": "string"}, "class": {"type": "string",
                                                     "description": "e.g. Box, Editable_Poly"},
       "layer": {"type": "string"}, "selected_only": {"type": "boolean"},
       "detail": {"type": "string", "enum": ["brief", "bbox", "count"]},
       "limit": {"type": "integer"}, "offset": {"type": "integer"}})
def list_objects(args):
    text = (args.get("name_contains") or "").lower()
    cls = (args.get("class") or "").lower()
    layer = (args.get("layer") or "").lower()
    source = rt.selection if args.get("selected_only") else rt.objects
    rows, counts = [], {}
    for node in source:
        if text and text not in _s(node.name).lower():
            continue
        if cls and cls != _s(rt.classOf(node)).lower():
            continue
        if layer and layer != _layer_name(node).lower():
            continue
        key = _s(rt.classOf(node))
        counts[key] = counts.get(key, 0) + 1
        rows.append(node)
    if args.get("detail") == "count":
        return {"total": len(rows), "by_class": counts}
    offset = max(0, int(args.get("offset") or 0))
    limit = max(1, min(MAX_ROWS, int(args.get("limit") or 200)))
    page = []
    for node in rows[offset:offset + limit]:
        item = _brief(node)
        if args.get("detail") == "bbox":
            item["bbox"] = _bbox(node)
        page.append(item)
    end = offset + len(page)
    return {"total": len(rows), "objects": page, "next_offset": end if end < len(rows) else None}


@tool("get_object", "Full detail of one object: transform, bounding box, material, modifiers, "
      "parent / children, wire colour, user properties.", {"name": {"type": "string"}}, ["name"])
def get_object(args):
    node = _node_by_name(args["name"])
    out = _brief(node)
    try:
        euler = rt.quatToEuler2(node.rotation)
        out["rotation"] = [_r(euler.x), _r(euler.y), _r(euler.z)]
    except Exception:
        pass
    out["scale"] = _p3(node.scale)
    out["bbox"] = _bbox(node)
    out["wirecolor"] = [int(node.wirecolor.r), int(node.wirecolor.g), int(node.wirecolor.b)]
    mat = node.material
    out["material"] = None if mat is None else {"name": _s(mat.name), "class": _s(rt.classOf(mat))}
    out["modifiers"] = [_s(m.name) for m in node.modifiers]
    out["parent"] = None if node.parent is None else _s(node.parent.name)
    out["children"] = [_s(c.name) for c in node.children]
    out["user_properties"] = _s(rt.getUserPropBuffer(node))
    return out


@tool("list_layers", "Layers with object counts, hidden / frozen state and the current layer.")
def list_layers(args):
    counts = {}
    for node in rt.objects:
        key = _layer_name(node)
        counts[key] = counts.get(key, 0) + 1
    out = []
    for i in range(int(rt.LayerManager.count)):
        layer = rt.LayerManager.getLayer(i)
        name = _s(layer.name)
        out.append({"name": name, "hidden": bool(layer.isHidden),
                    "frozen": bool(layer.isFrozen), "current": bool(layer.current),
                    "objects": counts.get(name, 0)})
    return {"layers": out}


@tool("list_materials", "Materials used in the scene (name, class, objects using each).",
      {"limit": {"type": "integer"}})
def list_materials(args):
    used = {}
    for node in rt.objects:
        mat = node.material
        if mat is None:
            continue
        key = (_s(mat.name), _s(rt.classOf(mat)))
        used.setdefault(key, []).append(_s(node.name))
    limit = max(1, min(MAX_ROWS, int(args.get("limit") or 200)))
    rows = [{"name": k[0], "class": k[1], "objects": len(v), "examples": v[:5]}
            for k, v in sorted(used.items())]
    return {"total": len(rows), "materials": rows[:limit]}


@tool("select_objects", "Select objects by name (replaces the selection unless add=true; "
      "empty names clears it). Changes no geometry.",
      {"names": S_NAMES, "add": {"type": "boolean"}})
def select_objects(args):
    names = args.get("names") or []
    if not names:
        rt.clearSelection()
        return {"selected": 0}
    nodes = _nodes(names)
    if not args.get("add"):
        rt.clearSelection()
    for node in nodes:
        rt.selectMore(node)
    return {"selected": len(list(rt.selection))}


PRIMITIVES = ("Box", "Sphere", "GeoSphere", "Cylinder", "Cone", "Tube", "Torus", "Pyramid",
              "Plane", "Teapot", "Capsule", "Point", "Dummy", "Text")


@tool("create_objects", "Create primitives. Each item: type (" + ", ".join(PRIMITIVES) + "), "
      "name, position [x,y,z], params (e.g. length, width, height, radius, segs, text, size), "
      "layer, wirecolor [r,g,b]. One undo step.",
      {"items": {"type": "array", "items": {"type": "object"}}}, ["items"], write=True)
def create_objects(args):
    created = []
    for item in args.get("items") or []:
        kind = _s(item.get("type")).strip()
        match = next((p for p in PRIMITIVES if p.lower() == kind.lower()), None)
        if match is None:
            raise ValueError("type '{}' is not one of {}.".format(kind, ", ".join(PRIMITIVES)))
        node = getattr(rt, match)()
        for key, value in (item.get("params") or {}).items():
            try:
                setattr(node, key, value)
            except Exception:
                raise ValueError("{} has no parameter '{}'.".format(match, key))
        if item.get("position") is not None:
            node.position = rt.point3(*[float(v) for v in item["position"]])
        if item.get("name"):
            node.name = _s(item["name"])
        if item.get("wirecolor") is not None:
            node.wirecolor = _color(item["wirecolor"])
        if item.get("layer"):
            _layer(item["layer"]).addNode(node)
        created.append(_brief(node))
    return {"created": created}


def _layer(name, create=True):
    layer = rt.LayerManager.getLayerFromName(name)
    if layer is None and create:
        layer = rt.LayerManager.newLayerFromName(name)
    if layer is None:
        raise ValueError("No layer named '{}'.".format(name))
    return layer


@tool("transform_objects", "Move / rotate / scale objects. move = [dx,dy,dz]; position = absolute "
      "[x,y,z]; rotate = [rx,ry,rz] degrees about each object's pivot; scale = factor or "
      "[sx,sy,sz]. One undo step.",
      {"names": S_NAMES, "move": S_XYZ, "position": S_XYZ, "rotate": S_XYZ,
       "scale": {"description": "number or [sx, sy, sz]"}}, ["names"], write=True)
def transform_objects(args):
    nodes = _nodes(args.get("names"))
    for node in nodes:
        if args.get("position") is not None:
            node.position = rt.point3(*[float(v) for v in args["position"]])
        if args.get("move") is not None:
            rt.move(node, rt.point3(*[float(v) for v in args["move"]]))
        if args.get("rotate") is not None:
            rx, ry, rz = [float(v) for v in args["rotate"]]
            rt.rotate(node, rt.eulerAngles(rx, ry, rz))
        if args.get("scale") is not None:
            s = args["scale"]
            factor = rt.point3(*([float(s)] * 3 if isinstance(s, (int, float)) else
                                 [float(v) for v in s]))
            rt.scale(node, factor)
    return {"objects": [_brief(n) for n in nodes]}


@tool("set_properties", "Rename, recolour, move to a layer, hide / unhide, set renderable or the "
      "user-property text of objects. One undo step.",
      {"names": S_NAMES, "new_name": {"type": "string", "description": "only with one name"},
       "wirecolor": {"type": "array", "items": {"type": "integer"}},
       "layer": {"type": "string"}, "hidden": {"type": "boolean"},
       "renderable": {"type": "boolean"}, "user_properties": {"type": "string"}},
      ["names"], write=True)
def set_properties(args):
    nodes = _nodes(args.get("names"))
    if args.get("new_name"):
        if len(nodes) != 1:
            raise ValueError("new_name needs exactly one object.")
        nodes[0].name = _s(args["new_name"])
    for node in nodes:
        if args.get("wirecolor") is not None:
            node.wirecolor = _color(args["wirecolor"])
        if args.get("layer"):
            _layer(args["layer"]).addNode(node)
        if args.get("hidden") is not None:
            node.isHidden = bool(args["hidden"])
        if args.get("renderable") is not None:
            node.renderable = bool(args["renderable"])
        if args.get("user_properties") is not None:
            rt.setUserPropBuffer(node, _s(args["user_properties"]))
    return {"objects": [_brief(n) for n in nodes]}


@tool("assign_material", "Create a material and assign it: type physical (default) or standard, "
      "name, color [r,g,b], roughness 0-1 (physical), opacity 0-100 (standard). One undo step.",
      {"names": S_NAMES, "name": {"type": "string"},
       "type": {"type": "string", "enum": ["physical", "standard"]},
       "color": {"type": "array", "items": {"type": "integer"}},
       "roughness": {"type": "number"}, "opacity": {"type": "number"}},
      ["names", "name"], write=True)
def assign_material(args):
    nodes = _nodes(args.get("names"))
    if (args.get("type") or "physical") == "standard":
        mat = rt.StandardMaterial()
        if args.get("color") is not None:
            mat.diffuse = _color(args["color"])
        if args.get("opacity") is not None:
            mat.opacity = float(args["opacity"])
    else:
        mat = rt.PhysicalMaterial()
        if args.get("color") is not None:
            mat.base_color = _color(args["color"])
        if args.get("roughness") is not None:
            mat.roughness = float(args["roughness"])
    mat.name = _s(args["name"])
    for node in nodes:
        node.material = mat
    return {"material": _s(mat.name), "objects": len(nodes)}


@tool("delete_objects", "Delete objects by name. One undo step (undo_last brings them back).",
      {"names": S_NAMES}, ["names"], write=True, destructive=True)
def delete_objects(args):
    nodes = _nodes(args.get("names"))
    for node in nodes:
        rt.delete(node)
    return {"deleted": len(nodes)}


@tool("zoom_extents", "Zoom the active viewport to all objects, or to the given objects.",
      {"names": S_NAMES})
def zoom_extents(args):
    if args.get("names"):
        nodes = _nodes(args["names"])
        rt.clearSelection()
        for node in nodes:
            rt.selectMore(node)
        rt.execute("max zoomext sel")
    else:
        rt.execute("max zoomext sel all")
    rt.redrawViews()
    return {"zoomed": len(args.get("names") or []) or "all"}


@tool("viewport_screenshot", "PNG of the active viewport as the user sees it.",
      {"max_width": {"type": "integer", "description": "default 1280"}})
def viewport_screenshot(args):
    rt.redrawViews()
    dib = rt.gw.getViewportDib()
    width = int(args.get("max_width") or 1280)
    if dib.width > width:
        small = rt.bitmap(width, int(dib.height * width / float(dib.width)))
        rt.copy(dib, small)
        dib = small
    size = {"width": int(dib.width), "height": int(dib.height)}
    path = os.path.join(tempfile.gettempdir(), "t3lab_max_view_{}.png".format(os.getpid()))
    dib.filename = path
    rt.save(dib)
    rt.close(dib)
    with open(path, "rb") as f:
        size["_image"] = base64.b64encode(f.read()).decode("ascii")
    return size


BLOCKED_MAXSCRIPT = ("doscommand", "hiddendoscommand", "shelllaunch", "deletefile",
                     "python.execute", "python.executefile", "dotnetobject \"system.diagnostics",
                     "createfile", "savemaxfile", "resetmaxfile", "quitmax")


@tool("run_maxscript", "Run MAXScript in the scene and return its result as text, for what the "
      "other tools cannot do. One undo step. Shell, file-writing, Python and quit calls are "
      "refused.", {"code": {"type": "string"}}, ["code"], write=True, destructive=True)
def run_maxscript(args):
    code = _s(args.get("code"))
    low = code.lower().replace(" ", "")
    for word in BLOCKED_MAXSCRIPT:
        if word.replace(" ", "") in low:
            raise ValueError("run_maxscript refuses '{}'.".format(word))
    return {"result": _s(rt.execute(code))}


@tool("undo_last", "Undo the last 3ds Max undo steps (default 1).",
      {"steps": {"type": "integer"}}, write=True)
def undo_last(args):
    steps = max(1, min(20, int(args.get("steps") or 1)))
    for _ in range(steps):
        rt.execute("max undo")
    return {"undone": steps}


TOOL_BY_NAME = dict((t["name"], t) for t in TOOLS)


def tools_list():
    """MCP tool definitions (what the bridge serves)."""
    out = []
    for t in TOOLS:
        out.append({"name": t["name"], "description": t["description"],
                    "inputSchema": t["schema"],
                    "annotations": {"readOnlyHint": not t["write"],
                                    "destructiveHint": t["destructive"],
                                    "openWorldHint": False}})
    return out


def run_tool(name, args):
    """Main thread: MCP-shaped result."""
    t = TOOL_BY_NAME.get(name)
    if t is None:
        return _error_result("Unknown tool '{}'.".format(name))
    try:
        if t["write"] and pymxs is not None and hasattr(pymxs, "undo"):
            with pymxs.undo(True, "T3Lab MCP: " + name):
                value = t["fn"](args or {})
        else:
            value = t["fn"](args or {})
    except Exception as ex:
        return _error_result("{}: {}".format(type(ex).__name__, ex))
    _STATE["document"] = _document()
    if isinstance(value, dict) and "_image" in value:
        image = value.pop("_image")
        return {"content": [{"type": "image", "data": image, "mimeType": "image/png"},
                            {"type": "text", "text": json.dumps(value, ensure_ascii=False)}]}
    return {"content": [{"type": "text", "text": json.dumps(value, ensure_ascii=False)}]}


def _error_result(message):
    return {"content": [{"type": "text", "text": message}], "isError": True}


# ─── server ──────────────────────────────────────────────────────────────────

_STATE = {"server": None, "port": None, "timer": None, "calls": 0, "document": "",
          "max": ""}
_JOBS = queue.Queue()


class _Job(object):
    def __init__(self, name, args):
        self.name, self.args = name, args
        self.done = threading.Event()
        self.result = None


def pump():
    """Main thread: run queued tool calls (Qt timer, or a test loop)."""
    while True:
        try:
            job = _JOBS.get_nowait()
        except queue.Empty:
            return
        try:
            job.result = run_tool(job.name, job.args)
        except Exception as ex:
            job.result = _error_result("Internal error: {}".format(ex))
        _STATE["calls"] += 1
        job.done.set()


def handle_line(line):
    """One request line -> reply line (socket thread)."""
    try:
        req = json.loads(line)
    except ValueError:
        return None
    rid, method, params = req.get("id"), req.get("method"), req.get("params") or {}
    if method == "health":
        result = {"app": APP, "version": VERSION, "pid": os.getpid(), "port": _STATE["port"],
                  "max": _STATE["max"], "active_document": _STATE["document"],
                  "calls": _STATE["calls"]}
    elif method == "tools/list":
        result = {"tools": tools_list()}
    elif method == "tools/call":
        job = _Job(params.get("name") or "", params.get("arguments") or {})
        _JOBS.put(job)
        if not job.done.wait(CALL_TIMEOUT):
            result = _error_result("3ds Max did not run the call within {} s (a dialog or a long "
                                   "render?).".format(int(CALL_TIMEOUT)))
        else:
            result = job.result
    else:
        return json.dumps({"id": rid, "error": {"code": -32601,
                                                "message": "Unknown method: {}".format(method)}})
    return json.dumps({"id": rid, "result": result}, ensure_ascii=False)


class _Handler(socketserver.StreamRequestHandler):
    def handle(self):
        for raw in self.rfile:
            reply = handle_line(raw.decode("utf-8", "replace"))
            if reply is None:
                return
            self.wfile.write((reply + "\n").encode("utf-8"))


class _Server(socketserver.ThreadingMixIn, socketserver.TCPServer):
    daemon_threads = True
    allow_reuse_address = False


def _bind(port_min=PORT_MIN, port_max=PORT_MAX):
    for port in range(port_min, port_max + 1):
        try:
            return _Server(("127.0.0.1", port), _Handler), port
        except OSError:
            continue
    raise OSError("No free port in {}-{}.".format(port_min, port_max))


def start(use_qt=True, port_min=PORT_MIN, port_max=PORT_MAX):
    """Start the server (idempotent). Returns the status line."""
    if _STATE["server"] is not None:
        return status()
    server, port = _bind(port_min, port_max)
    _STATE.update(server=server, port=port)
    try:
        _STATE["max"] = _s(rt.maxVersion()[0]) if rt is not None else ""
        _STATE["document"] = _document() if rt is not None else ""
    except Exception:
        pass
    threading.Thread(target=server.serve_forever, name="T3LabMax MCP", daemon=True).start()
    if use_qt:
        QtCore = _qt_core()
        timer = QtCore.QTimer()
        timer.timeout.connect(pump)
        timer.start(PUMP_MS)
        _STATE["timer"] = timer
    return status()


def stop():
    server = _STATE.get("server")
    if server is not None:
        server.shutdown()
        server.server_close()
    if _STATE.get("timer") is not None:
        _STATE["timer"].stop()
    _STATE.update(server=None, port=None, timer=None)
    return status()


def status():
    if _STATE["server"] is None:
        return "T3LabMax MCP v{}: stopped".format(VERSION)
    return "T3LabMax MCP v{}: listening on 127.0.0.1:{} | {} tools | {} calls".format(
        VERSION, _STATE["port"], len(TOOLS), _STATE["calls"])


def main():
    """Entry from t3lab_max_mcp_start.ms. Never raises into 3ds Max start-up."""
    try:
        print(start())
    except Exception:
        print("T3LabMax MCP did not start:\n" + traceback.format_exc())

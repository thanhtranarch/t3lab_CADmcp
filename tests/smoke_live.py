"""Live test through the bridge against a running AutoCAD with the T3LabCAD add-in.

    python -I tests/smoke_live.py [screenshot_dir]

Read-only calls run on the drawing that is open. Every write runs on a NEW scratch
drawing (new_document) that is closed without saving at the end; the test stops
before writing if the scratch drawing did not become active.
"""

import base64
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from smoke_bridge import BRIDGE, check  # noqa: E402

import shutil  # noqa: E402
import subprocess  # noqa: E402
import tempfile  # noqa: E402
import time  # noqa: E402


class Client:
    def __init__(self):
        env = dict(os.environ)
        env.pop("T3LABCAD_PORT_RANGE", None)
        self.proc = subprocess.Popen([sys.executable, "-I", BRIDGE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.DEVNULL, env=env)
        self.n = 0
        self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}})

    def request(self, method, params=None):
        self.n += 1
        self.proc.stdin.write((json.dumps({"jsonrpc": "2.0", "id": self.n, "method": method, "params": params or {}}) + "\n").encode())
        self.proc.stdin.flush()
        while True:
            reply = json.loads(self.proc.stdout.readline())
            if reply.get("id") == self.n:
                return reply

    def call(self, tool, **args):
        result = self.request("tools/call", {"name": tool, "arguments": args})["result"]
        text = next((b["text"] for b in result["content"] if b["type"] == "text"), "")
        try:
            data = json.loads(text)
        except ValueError:
            data = text
        return result, data


def main():
    shots = sys.argv[1] if len(sys.argv) > 1 else os.environ.get("TEMP", ".")
    c = Client()
    tools = [t["name"] for t in c.request("tools/list")["result"]["tools"]]
    check("tools/list live ({} tools)".format(len(tools)), "add_entities" in tools and "list_autocad_instances" in tools, tools)

    r, status = c.call("get_status")
    check("get_status", not r.get("isError") and "documents" in status, status)
    user_doc = status.get("active_document", {}).get("name")
    if user_doc:
        read_only_checks(c, status, shots)
    else:
        print("   no drawing open: skipping the read-only checks on the user's drawing")

    # ---- writes on a scratch drawing
    r, d = c.call("new_document")
    check("new_document", not r.get("isError"), d)
    r, status = c.call("get_status")
    scratch = status.get("active_document", {}).get("name")
    if r.get("isError") or not scratch or scratch == user_doc:
        print("scratch drawing is not active; stopping before any write")
        return 1
    print("   scratch drawing:", scratch)

    # switching drawings from inside a drawing's context (this used to hang)
    r, d = c.call("new_document")
    check("new_document while another drawing is active", not r.get("isError"), d)
    second = d["created"]["name"] if isinstance(d, dict) else None
    r, d = c.call("activate_document", document=scratch)
    check("activate_document", not r.get("isError") and d["active"]["name"] == scratch, d)
    r, d = c.call("close_document", document=second, discard_changes=True)
    check("close_document (other drawing)", not r.get("isError"), d)
    r, status = c.call("get_status")
    check("scratch still active", status["active_document"]["name"] == scratch, status.get("active_document"))

    write_checks(c, shots)
    named_object_checks(c)
    geometry_checks(c)
    annotation_checks(c)
    viewport_checks(c)
    tmp = os.path.join(tempfile.gettempdir(), "t3labcad_live_{}".format(os.getpid()))
    if not file_checks(c, tmp):
        print("scratch drawing is not active after the file checks; stopping")
        return 1
    c.call("add_entities", items=[{"type": "point", "position": [0, 0]}])  # leave an unsaved change

    r, d = c.call("close_document")
    check("close_document refuses unsaved", r.get("isError") and "unsaved" in d, d)
    r, d = c.call("close_document", discard_changes=True)
    check("close_document discard", not r.get("isError"), d)
    r, status = c.call("get_status")
    check("back to the user's drawing (or none)", status.get("active_document", {}).get("name") == user_doc, status.get("active_document"))
    shutil.rmtree(tmp, ignore_errors=True)

    # ---- hot reload of the Core
    before = c.call("list_autocad_instances")[1]["instances"][0]["calls"]
    r, d = c.call("reload_addin")
    check("reload_addin accepted", not r.get("isError"), d)
    reloaded = False
    for _ in range(20):
        time.sleep(1)
        r, d = c.call("list_autocad_instances")
        if not r.get("isError") and d["instances"][0]["calls"] < before:
            reloaded = True
            break
    check("Core reloaded (call counter reset)", reloaded, d)
    r, status = c.call("get_status")
    check("tools work after reload", not r.get("isError"), status)

    print("\n{} failure(s)".format(check.failed))
    return 1 if check.failed else 0


def read_only_checks(c, status, shots):
    print("   user drawing:", status["active_document"]["name"], "| entities:", status["active_document"]["model_space_entities"],
          "| units:", status["active_document"]["insunits"])
    r, d = c.call("list_layers")
    check("list_layers ({})".format(d.get("count") if isinstance(d, dict) else "?"), not r.get("isError"), d)
    r, d = c.call("list_layouts")
    check("list_layouts", not r.get("isError") and d["layouts"], d)
    r, d = c.call("list_blocks")
    check("list_blocks ({})".format(d.get("count") if isinstance(d, dict) else "?"), not r.get("isError"), d)
    r, d = c.call("query_entities", space="model", detail="count")
    check("query_entities count ({})".format(d.get("total_matched") if isinstance(d, dict) else "?"), not r.get("isError"), d)
    r, d = c.call("query_entities", space="model", limit=3)
    check("query_entities page", not r.get("isError") and d["returned"] <= 3, d)
    r, d = c.call("get_texts", space="model", limit=5, sort="reading")
    check("get_texts ({} total)".format(d.get("total") if isinstance(d, dict) else "?"), not r.get("isError"), d)
    r, d = c.call("screenshot", width=1000, height=640, save_path=os.path.join(shots, "t3labcad_user_view.png"))
    img = next((b for b in r["content"] if b["type"] == "image"), None)
    check("screenshot user view", img and base64.b64decode(img["data"])[:4] == b"\x89PNG", r if not img else "")


def write_checks(c, shots):
    r, d = c.call("set_layer", name="T3-TEST", color=1, linetype="DASHED", make_current=True)
    check("set_layer create", not r.get("isError") and d["created"], d)
    items = [
        {"type": "line", "start": [0, 0], "end": [1000, 0], "layer": "T3-WALL", "color": "ByLayer"},
        {"type": "polyline", "points": [[0, 100], [500, 100], [500, 400]], "closed": False, "bulges": [0, 0.5, 0]},
        {"type": "rectangle", "corner1": [0, 500], "corner2": [400, 800]},
        {"type": "circle", "center": [700, 650], "radius": 120, "color": 3},
        {"type": "arc", "points": [[900, 500], [1000, 600], [1100, 500]]},
        {"type": "ellipse", "center": [1300, 650], "major_axis": [150, 0], "ratio": 0.5},
        {"type": "point", "position": [1500, 0]},
        {"type": "text", "position": [200, 650], "text": "PHÒNG KHÁCH", "height": 50, "align": "middle_center"},
        {"type": "mtext", "position": [0, -100], "text": "Line 1\nLine 2", "height": 40, "width": 600},
        {"type": "dim_aligned", "p1": [0, 0], "p2": [1000, 0], "dim_point": [500, -300]},
        {"type": "dim_linear", "p1": [0, 500], "p2": [0, 800], "dim_point": [-200, 650], "rotation": 90},
        {"type": "hatch", "points": [[0, 500], [400, 500], [400, 800], [0, 800]], "pattern": "ANSI31", "scale": 10},
        {"type": "spline", "points": [[1200, 0], [1300, 100], [1400, 0], [1500, 100]]},
    ]
    r, d = c.call("add_entities", items=items)
    check("add_entities ({} types)".format(len(items)), not r.get("isError") and d["created_count"] == len(items), d)
    if r.get("isError"):
        print("   ", d)
    handles = {i["type"]: i["handle"] for i in d.get("created", [])} if isinstance(d, dict) else {}

    r, d = c.call("add_entities", items=[{"type": "circle", "center": [0, 0], "radius": -1}])
    check("add_entities rejects bad item (atomic)", r.get("isError") and "Nothing was created" in d, d)

    r, d = c.call("query_entities", handles=list(handles.values()), detail="full")
    check("query_entities full by handle", not r.get("isError") and d["total_matched"] == len(handles), d)
    r, d = c.call("query_entities", types=["CIRCLE", "ARC"], layers=["T3-*"])
    check("query_entities type+layer filter", not r.get("isError") and d["total_matched"] == 2, d)
    r, d = c.call("query_entities", window=[[-10, 450], [450, 850]], window_mode="inside")
    check("query_entities window inside", not r.get("isError") and d["total_matched"] >= 2, d)
    r, d = c.call("get_texts", contains="khách")
    check("get_texts unicode contains", not r.get("isError") and d["total"] == 1, d)

    r, d = c.call("update_entities", items=[
        {"handle": handles["circle"], "radius": 150, "color": "#FF8800"},
        {"handle": handles["text"], "text": "BẾP", "height": 60},
        {"handle": handles["line"], "end": [1200, 0], "lineweight": 50, "rotation": 5},
    ])
    ok = not r.get("isError") and d["updated"] == 3 and d["results"][2].get("skipped") == ["rotation"]
    check("update_entities (+skipped report)", ok, d)

    r, d = c.call("transform_entities", handles=[handles["circle"]], operation="move", displacement=[0, 300], copy=True)
    check("transform copy", not r.get("isError") and d["copied"] == 1, d)
    r, d = c.call("transform_entities", handles=[handles["rectangle"]], operation="rotate", base_point=[0, 500], angle=15)
    check("transform rotate", not r.get("isError") and d["transformed"] == 1, d)

    r, d = c.call("create_block", name="T3_TEST_BLOCK", handles=[handles["ellipse"], handles["point"]], base_point=[1300, 650])
    check("create_block convert", not r.get("isError") and d["insert_handle"], d)
    r, d = c.call("add_entities", items=[{"type": "insert", "block": "T3_TEST_BLOCK", "position": [1300, 1200], "scale": 2, "rotation": 30}])
    check("insert block", not r.get("isError"), d)
    r, d = c.call("list_blocks", name="T3_*")
    check("list_blocks insert count", not r.get("isError") and d["blocks"][0]["inserts"] == 2, d)

    r, d = c.call("selection", action="set", handles=[handles["line"], handles["mtext"]])
    check("selection set", not r.get("isError") and d["selected"] == 2, d)
    r, d = c.call("selection", action="get")
    check("selection get", not r.get("isError") and d["count"] == 2, d)
    c.call("selection", action="clear")

    r, d = c.call("set_sysvar", name="USERI1", value=42)
    check("set_sysvar USERI1", not r.get("isError") and d["new"] == 42, d)
    r, d = c.call("get_sysvars", names=["USERI1", "INSUNITS", "NOPE_VAR"])
    check("get_sysvars", not r.get("isError") and d["USERI1"] == 42, d)

    r, d = c.call("run_command", command="_.LINE 2000,0 2500,500  ")
    check("run_command LINE creates 1 entity", not r.get("isError") and d["finished"] and d["created"]["count"] == 1, d)
    r, d = c.call("run_command", command="_.LINE 3000,0 3500,500 ", wait_seconds=2)
    check("run_command cancels unfinished command", not r.get("isError") and "cancelled_unfinished" in d and d["finished"], d)
    r, status = c.call("get_status")
    check("not busy afterwards", status["active_document"]["command_in_progress"] == "", status.get("active_document"))
    r, d = c.call("run_command", command="(alert \"x\")")
    check("run_command rejects AutoLISP", r.get("isError"), d)
    r, d = c.call("run_command", command="_.SHELL dir")
    check("run_command rejects SHELL", r.get("isError"), d)

    r, d = c.call("delete_layer", name="T3-TEST")
    check("delete_layer refuses current layer", r.get("isError"), d)
    c.call("set_layer", name="0", make_current=True)
    r, d = c.call("delete_layer", name="T3-TEST", merge_into="T3-WALL")
    check("delete_layer merge", not r.get("isError") and d["entities_moved"] > 0, d)

    r, d = c.call("zoom", mode="extents")
    check("zoom extents", not r.get("isError"), d)
    r, d = c.call("screenshot", save_path=os.path.join(shots, "t3labcad_scratch.png"))
    check("screenshot scratch", any(b["type"] == "image" for b in r["content"]), d)
    r, d = c.call("delete_entities", handles=[handles["spline"]])
    check("delete_entities", not r.get("isError") and d["erased"] == 1, d)


def ok(r):
    return not r.get("isError")


def named_object_checks(c):
    """Create / rename / delete for layouts, styles, linetypes, blocks, groups, views; explode; purge (scratch drawing)."""
    # layouts + viewports
    r, d = c.call("create_layout", name="T3 Sheet A")
    check("create_layout", ok(r) and d["layout"] == "T3 Sheet A", d)
    r, d = c.call("create_layout", name="T3 Sheet B", copy_from="T3 Sheet A", activate=True)
    check("create_layout copy + activate", ok(r) and d["current_layout"] == "T3 Sheet B", d)
    r, d = c.call("add_viewport", layout="T3 Sheet B", corner1=[20, 20], corner2=[200, 150], view_window=[[0, -300], [1500, 1200]])
    check("add_viewport fit window", ok(r) and d.get("handle"), d)
    r, d = c.call("add_viewport", layout="T3 Sheet B", corner1=[210, 20], corner2=[280, 150], view_center=[700, 650], scale="1:20")
    check("add_viewport 1:20", ok(r) and abs(d["scale"] - 0.05) < 1e-9 and d["scale_text"] == "1:20", d)
    r, d = c.call("rename_object", kind="layout", name="T3 Sheet A", new_name="T3 Sheet A2")
    check("rename_object layout", ok(r), d)
    r, d = c.call("delete_object", kind="layout", names=["T3 Sheet A2"])
    check("delete_object layout", ok(r) and d["deleted"] == 1, d)
    r, d = c.call("delete_object", kind="layout", names=["Model"])
    check("delete_object refuses Model", r.get("isError"), d)
    r, d = c.call("list_named_objects", kind="layout")
    check("list_named_objects layout", ok(r) and any(l["name"] == "T3 Sheet B" for l in d["layouts"]), d)
    c.call("activate_layout", name="Model")

    # text styles
    r, d = c.call("set_text_style", name="T3-Arial", font="Arial", width_factor=0.8, make_current=True)
    check("set_text_style TrueType", ok(r) and d["created"] and d["style"]["font"] == "Arial", d)
    r, d = c.call("set_text_style", name="T3-Romans", font="romans.shx", oblique=15)
    check("set_text_style SHX", ok(r) and d["style"]["font"].lower() == "romans.shx", d)
    r, d = c.call("set_text_style", name="T3-Bad", font="NoSuchFont123")
    check("set_text_style rejects unknown font", r.get("isError"), d)
    r, d = c.call("list_named_objects", kind="text_style", name="T3-*")
    check("list_named_objects text_style", ok(r) and d["count"] == 2, d)
    r, d = c.call("delete_object", kind="text_style", names=["T3-Arial"])
    check("delete_object refuses current text style", r.get("isError"), d)
    c.call("set_text_style", name="Standard", make_current=True)
    r, d = c.call("rename_object", kind="text_style", name="T3-Romans", new_name="T3-Simplex")
    check("rename_object text_style", ok(r), d)
    r, d = c.call("delete_object", kind="text_style", names=["T3-Simplex"])
    check("delete_object text_style", ok(r) and d["deleted"] == 1, d)

    # dimension styles
    r, d = c.call("set_dim_style", name="T3-100", props={"DIMSCALE": 100, "DIMTXT": 2.5, "dimasz": 2, "DIMTXSTY": "T3-Arial",
                                                         "DIMBLK": "_ARCHTICK", "DIMCLRD": 1})
    check("set_dim_style create", ok(r) and d["created"] and "DIMSCALE" in d["changed"], d)
    r, d = c.call("set_dim_style", name="T3-100", props={"DIMNOPE": 1})
    check("set_dim_style rejects unknown variable", r.get("isError"), d)
    r, d = c.call("list_named_objects", kind="dim_style", name="T3-100", full=True)
    item = d["items"][0] if ok(r) and d["items"] else {}
    check("dim_style full read back", item.get("DIMSCALE") == 100 and item.get("DIMTXSTY") == "T3-Arial" and
          item.get("DIMBLK", "").upper() == "_ARCHTICK", item)
    r, d = c.call("set_dim_style", name="T3-100", new_name="T3-DIM100", make_current=True)
    check("set_dim_style rename + current", ok(r) and d["dim_style"] == "T3-DIM100" and d["style"].get("current"), d)
    c.call("set_dim_style", name="Standard", make_current=True)

    # linetypes
    r, d = c.call("load_linetype")
    check("load_linetype lists the file", ok(r) and d["count"] > 10, d)
    r, d = c.call("load_linetype", names=["HIDDEN*", "CENTER", "NOPE_LT"])
    check("load_linetype wildcard", ok(r) and "HIDDEN2" in d["loaded"] + d["already_loaded"] and d.get("not_in_file") == ["NOPE_LT"], d)
    r, d = c.call("delete_object", kind="linetype", names=["HIDDEN2"])
    check("delete_object linetype", ok(r), d)
    r, d = c.call("delete_object", kind="linetype", names=["Continuous"])
    check("delete_object refuses Continuous", r.get("isError"), d)

    # blocks
    r, d = c.call("rename_object", kind="block", name="T3_TEST_BLOCK", new_name="T3_BLOCK_RENAMED")
    check("rename_object block", ok(r), d)
    r, d = c.call("delete_object", kind="block", names=["T3_BLOCK_RENAMED"])
    check("delete_object refuses block with inserts", r.get("isError") and "insert" in d, d)
    r, d = c.call("delete_object", kind="block", names=["T3_BLOCK_RENAMED"], erase_references=True)
    check("delete_object block + inserts", ok(r) and d["results"][0].get("inserts_erased") == 2, d)

    # groups
    r, d = c.call("add_entities", items=[{"type": "line", "start": [0, 2000], "end": [500, 2000]},
                                         {"type": "circle", "center": [800, 2000], "radius": 100}])
    members = [i["handle"] for i in d["created"]] if ok(r) else []
    r, d = c.call("set_group", name="T3-GRP", handles=members, description="smoke test")
    check("set_group create", ok(r) and d["created"] and d["entities"] == 2, d)
    r, d = c.call("set_group", name="T3-GRP", remove_handles=members[:1])
    check("set_group remove", ok(r) and d["entities"] == 1, d)
    r, d = c.call("rename_object", kind="group", name="T3-GRP", new_name="T3-GRP2")
    check("rename_object group", ok(r), d)
    r, d = c.call("list_named_objects", kind="group")
    check("list_named_objects group", ok(r) and any(g["name"] == "T3-GRP2" for g in d["items"]), d)
    r, d = c.call("delete_object", kind="group", names=["T3-GRP2"])
    check("delete_object group (ungroup)", ok(r) and d["results"][0].get("entities_kept") == 1, d)

    # named views
    r, d = c.call("save_view", name="T3-V1", p1=[0, 0], p2=[1000, 800])
    check("save_view window", ok(r) and d["created"], d)
    r, d = c.call("save_view", name="T3-V1")
    check("save_view refuses duplicate", r.get("isError"), d)
    r, d = c.call("restore_view", name="T3-V1")
    check("restore_view", ok(r), d)
    r, d = c.call("rename_object", kind="view", name="T3-V1", new_name="T3-V2")
    check("rename_object view", ok(r), d)
    r, d = c.call("delete_object", kind="view", names=["T3-V2", "NO_SUCH_VIEW"])
    check("delete_object partial result", ok(r) and d["deleted"] == 1 and not d["results"][1]["ok"], d)

    # explode
    r, d = c.call("add_entities", items=[{"type": "rectangle", "corner1": [0, 3000], "corner2": [300, 3200]}])
    rect = d["created"][0]["handle"] if ok(r) else None
    r, d = c.call("explode_entities", handles=[rect])
    check("explode rectangle -> 4 lines", ok(r) and d["new_count"] == 4, d)
    r, d = c.call("explode_entities", handles=[d["new_handles"][0]] if ok(r) else [])
    check("explode refuses a line", r.get("isError"), d)

    # purge
    c.call("set_layer", name="T3-UNUSED")
    r, d = c.call("purge", dry_run=True)
    check("purge dry_run reports", ok(r) and "T3-UNUSED" in d["would_purge"].get("layer", []), d)
    r, d = c.call("list_layers", name="T3-UNUSED")
    check("dry_run changed nothing", ok(r) and d["count"] == 1, d)
    r, d = c.call("purge", kinds=["layer"], names=["T3-UNUSED"])
    check("purge one layer", ok(r) and d["purged"].get("layer") == ["T3-UNUSED"], d)


def geometry_checks(c):
    """measure, length / area in query_entities, offset / join / boundary, intersections, takeoff, one undo step per call."""
    r, d = c.call("add_entities", items=[{"type": "rectangle", "corner1": [0, -6000], "corner2": [1000, -5500]}])
    rect = d["created"][0]["handle"] if ok(r) else None
    r, d = c.call("query_entities", handles=[rect], detail="full")
    e = d["entities"][0] if ok(r) else {}
    check("query_entities area / length", e.get("area") == 500000 and e.get("length") == 3000, e)
    r, d = c.call("query_entities", types=["LWPOLYLINE"], closed=True, totals=True)
    check("query_entities closed filter + totals", ok(r) and d["total_area"] >= 500000, d)
    r, d = c.call("measure", mode="area", handles=[rect])
    check("measure area by handle", ok(r) and d["total"] == 500000, d)
    r, d = c.call("measure", mode="distance", points=[[0, 0], [3, 4], [3, 10]])
    check("measure distance chain", ok(r) and d["total"] == 11, d)
    r, d = c.call("measure", mode="angle", points=[[0, 0], [1, 0], [0, 1]])
    check("measure angle", ok(r) and d["angle"] == 90, d)
    r, d = c.call("measure", mode="area", points=[[0, 0], [10, 0], [10, 10], [0, 10]])
    check("measure area of points", ok(r) and d["area"] == 100 and d["perimeter"] == 40, d)

    r, d = c.call("edit_geometry", operation="offset", handles=[rect], distance=100, side_point=[-500, -6500])
    created = d.get("created", []) if ok(r) else []
    check("offset outward (sharp corners)", len(created) == 1 and abs(created[0].get("area", 0) - 840000) < 1, d)
    r, d = c.call("edit_geometry", operation="offset", handles=[rect], distance=100)
    check("offset needs a side", r.get("isError"), d)

    r, d = c.call("add_entities", items=[{"type": "line", "start": [0, -7000], "end": [500, -7000]},
                                         {"type": "line", "start": [500, -7000], "end": [500, -6800]},
                                         {"type": "arc", "center": [500, -6600], "radius": 200, "start_angle": 270, "end_angle": 0}])
    lines = [i["handle"] for i in d["created"]] if ok(r) else []
    r, d = c.call("edit_geometry", operation="join", handles=lines)
    check("join lines + arc into a polyline", ok(r) and d["joined"] == 3 and d["result"]["type"] == "LWPOLYLINE", d)

    c.call("zoom", mode="window", p1=[-300, -6300], p2=[1300, -5200])
    r, d = c.call("edit_geometry", operation="boundary", point=[500, -5750])
    areas = [x.get("area") for x in d.get("created", [])] if ok(r) else []
    check("boundary inside the rectangle", any(a and abs(a - 500000) < 1 for a in areas), d)

    r, d = c.call("add_entities", items=[{"type": "line", "start": [-100, -5750], "end": [1100, -5750]}])
    cross = d["created"][0]["handle"] if ok(r) else None
    r, d = c.call("measure", mode="intersections", handles=[cross, rect])
    check("measure intersections", ok(r) and d["count"] == 2, d)
    r, d = c.call("measure", mode="closest", handles=[cross], point=[500, -5000])
    check("measure closest", ok(r) and d["distance"] == 750, d)

    r, d = c.call("quantity_takeoff", types=["LWPOLYLINE"], group_by="type")
    check("quantity_takeoff by type", ok(r) and d["groups"] and d["groups"][0]["type"] == "LWPOLYLINE" and d["groups"][0]["area"] > 0, d)

    before = c.call("query_entities", space="model", detail="count")[1]["total_matched"]
    c.call("add_entities", items=[{"type": "point", "position": [0, -8000]}, {"type": "point", "position": [10, -8000]}])
    r, d = c.call("run_command", command="_.U")
    after = c.call("query_entities", space="model", detail="count")[1]["total_matched"]
    check("one add_entities call = one undo step", ok(r) and after == before, (before, after))


def annotation_checks(c):
    """table, mleader, more dimension types, dim_chain, replace_text, attdef + block attributes."""
    rows = [["Phòng", "Diện tích"], ["Khách", "25"], ["Bếp", "12"]]
    r, d = c.call("add_entities", items=[{"type": "table", "position": [0, -9000], "rows": rows, "title": "BẢNG PHÒNG",
                                          "col_widths": [800, 500], "row_height": 120, "text_height": 50}])
    check("add table", ok(r), d)
    table = d["created"][0]["handle"] if ok(r) else None
    r, d = c.call("query_entities", handles=[table], detail="full")
    cells = d["entities"][0].get("cells", []) if ok(r) else []
    check("table read back", len(cells) == 4 and cells[0][0] == "BẢNG PHÒNG" and cells[2][0] == "Khách", cells)
    r, d = c.call("update_entities", items=[{"handle": table, "cells": [{"row": 3, "col": 1, "text": "15"}]}])
    check("update table cell", ok(r) and d["updated"] == 1, d)

    r, d = c.call("add_entities", items=[{"type": "mleader", "points": [[0, -10000], [300, -9700], [600, -9700]], "text": "Ghi chú"}])
    check("add mleader", ok(r), d)
    r, d = c.call("get_texts", contains="Ghi chú")
    check("mleader text readable", ok(r) and d["total"] == 1, d)

    r, d = c.call("add_entities", items=[{"type": "circle", "center": [3000, -10000], "radius": 200},
                                         {"type": "arc", "center": [3600, -10000], "radius": 200, "start_angle": 0, "end_angle": 90}])
    circle, arc = (d["created"][0]["handle"], d["created"][1]["handle"]) if ok(r) else (None, None)
    r, d = c.call("add_entities", items=[
        {"type": "dim_radius", "handle": circle},
        {"type": "dim_diameter", "handle": circle, "angle": 135},
        {"type": "dim_angular", "center": [0, -11000], "p1": [500, -11000], "p2": [0, -10500]},
        {"type": "dim_arc", "handle": arc},
        {"type": "dim_ordinate", "point": [500, -11000], "leader_end": [500, -11500]},
    ])
    check("add 5 dimension types", ok(r) and d["created_count"] == 5, d)
    dims = [i["handle"] for i in d.get("created", [])] if ok(r) else []
    r, d = c.call("query_entities", handles=dims)
    m = [e.get("measurement") for e in d.get("entities", [])] if ok(r) else []
    deg = d["entities"][2].get("measurement_deg") if ok(r) and len(d.get("entities", [])) == 5 else None
    check("dimension values (200, 400, 90 deg, arc, X=500)", len(m) == 5 and m[0] == 200 and m[1] == 400 and deg == 90 and m[4] == 500, (m, deg))
    for mode in ("continue", "baseline"):
        r, d = c.call("add_entities", items=[{"type": "dim_chain", "points": [[0, -12000], [1000, -12000], [2500, -12000], [3000, -12000]],
                                              "dim_point": [0, -12300], "mode": mode}])
        check("dim_chain " + mode, ok(r) and len(d["created"][0]["handles"]) == 3, d)

    r, d = c.call("replace_text", find="bếp", replace="Bếp mới", dry_run=True)
    check("replace_text dry run (Vietnamese, any case)", ok(r) and d["total_matches"] >= 2 and d["matches"].get("table", 0) >= 1, d)
    r, d = c.call("query_entities", handles=[table], detail="full")
    check("dry run changed nothing", ok(r) and d["entities"][0]["cells"][3][0] == "Bếp", d)
    r, d = c.call("replace_text", find="Khách", replace="Phòng khách", types=["table"])
    check("replace_text in a table", ok(r) and d["changed_entities"] == 1, d)
    r, d = c.call("query_entities", handles=[table], detail="full")
    check("table cell replaced", ok(r) and d["entities"][0]["cells"][2][0] == "Phòng khách", d)

    # formatting codes survive: the match is on the visible text, \\P and {\\H...;} stay as they are
    r, d = c.call("add_entities", items=[{"type": "mtext", "position": [0, -14000], "text": "Bàn\\P{\\H1.5x;ghế} A", "height": 50}])
    mt = d["created"][0]["handle"] if ok(r) else None
    r, d = c.call("replace_text", find="ghế", replace="tủ", types=["mtext"])
    check("replace_text in formatted MTEXT", ok(r) and d["changed_entities"] == 1, d)
    r, d = c.call("query_entities", handles=[mt], detail="full")
    check("MTEXT codes kept", ok(r) and d["entities"][0]["contents"] == "Bàn\\P{\\H1.5x;tủ} A", d)
    r, d = c.call("replace_text", find="A", replace="B", types=["mtext"], whole_word=True, dry_run=True)
    check("codes not matched ('A' only once, not in \\H)", ok(r) and d["total_matches"] == 1, d)

    r, d = c.call("add_entities", items=[{"type": "rectangle", "corner1": [5000, -9000], "corner2": [5400, -8700]},
                                         {"type": "attdef", "position": [5050, -8900], "tag": "ROOM", "prompt": "Room name",
                                          "default": "P01", "height": 60}])
    parts = [i["handle"] for i in d["created"]] if ok(r) else []
    r, d = c.call("create_block", name="T3_ROOM", handles=parts, base_point=[5000, -9000])
    check("create_block with an attribute definition", ok(r) and d["insert_handle"], d)
    room = d.get("insert_handle") if ok(r) else None
    r, d = c.call("extract_block_data", block_names=["T3_ROOM"])
    check("insert made by create_block has no attributes yet", ok(r) and d["count"] == 1 and d["rows"][0]["attributes"] == {}, d)
    r, d = c.call("sync_block_attributes", names=["T3_ROOM"])
    check("sync_block_attributes adds the attribute", ok(r) and d["blocks"][0]["attributes_added"] == 1, d)
    r, d = c.call("update_entities", items=[{"handle": room, "attributes": {"ROOM": "Khách"}}])
    r, d = c.call("extract_block_data", block_names=["T3_ROOM"])
    check("extract_block_data values", ok(r) and d["rows"][0]["attributes"].get("ROOM") == "Khách", d)
    r, d = c.call("sync_block_attributes", names=["T3_ROOM"])
    check("sync keeps values", ok(r) and d["blocks"][0]["attributes_updated"] == 1 and d["blocks"][0]["attributes_added"] == 0, d)

    r, d = c.call("add_entities", items=[{"type": "polyline", "points": [[0, -13000], [100, -13000]]}])
    pl = d["created"][0]["handle"] if ok(r) else None
    r, d = c.call("update_entities", items=[{"handle": pl, "points": [[0, -13000], [100, -13000], [100, -12900]], "draw_order": "bottom"}])
    r, d = c.call("query_entities", handles=[pl], detail="full")
    check("update polyline vertices", ok(r) and d["entities"][0]["vertex_count"] == 3, d)


def viewport_checks(c):
    """set_viewport: scale, lock, per-viewport layer freeze."""
    c.call("create_layout", name="T3 VP")
    r, d = c.call("add_viewport", layout="T3 VP", corner1=[20, 20], corner2=[200, 150], view_center=[500, 500], scale="1:10")
    vp = d.get("handle") if ok(r) else None
    r, d = c.call("set_viewport", handle=vp, scale="1:50", locked=True)
    check("set_viewport scale + lock", ok(r) and abs(d["scale"] - 0.02) < 1e-9 and d["locked"], d)
    r, d = c.call("set_viewport", handle=vp, scale="1:20")
    check("set_viewport refuses a locked view without locked", r.get("isError"), d)
    r, d = c.call("set_viewport", handle=vp, scale="1:20", locked=True)
    check("set_viewport keeps it locked", ok(r) and d["scale_text"] == "1:20" and d["locked"], d)
    r, d = c.call("set_viewport", handle=vp, freeze_layers=["T3-WALL"])
    check("set_viewport freeze layer", ok(r) and "T3-WALL" in d["frozen_layers"], d)
    r, d = c.call("set_viewport", handle=vp, thaw_layers=["T3-*"])
    check("set_viewport thaw layer", ok(r) and "T3-WALL" not in d["frozen_layers"], d)
    c.call("activate_layout", name="Model")
    r, d = c.call("delete_object", kind="layout", names=["T3 VP"])
    check("delete the test layout", ok(r), d)


def is_pdf(path):
    if not os.path.exists(path):
        return False
    with open(path, "rb") as f:
        return f.read(4) == b"%PDF"


def dwg_version(path):
    with open(path, "rb") as f:
        return f.read(6).decode("ascii", "replace")


def file_checks(c, tmp):
    """Save / copy / version / list / xref / rename / delete files in a temp folder. Returns False if the scratch drawing got lost."""
    os.makedirs(tmp, exist_ok=True)
    main = os.path.join(tmp, "t3_main.dwg")
    copy = os.path.join(tmp, "t3_copy.dwg")
    old = os.path.join(tmp, "t3_copy2013.dwg")
    c.call("set_layer", name="T3-SRC", color=5)  # unused layer, for import_from_drawing below
    r, d = c.call("save_document", path=main)
    check("save_document Save As", ok(r) and os.path.exists(main), d)
    r, d = c.call("save_document", path=copy, copy=True)
    check("save_document copy", ok(r) and os.path.exists(copy) and dwg_version(copy) == "AC1032", d)
    r, d = c.call("save_document", path=old, copy=True, version="2013")
    check("save_document copy as 2013", ok(r) and os.path.exists(old) and dwg_version(old) == "AC1027", d)
    r, status = c.call("get_status")
    check("Save As renamed the drawing, copies did not", status["active_document"]["name"] == "t3_main.dwg", status.get("active_document"))

    r, d = c.call("list_drawing_files", folder=tmp)
    names = {f["name"]: f for f in d.get("files", [])} if ok(r) else {}
    check("list_drawing_files", ok(r) and d["total"] == 3 and names.get("t3_main.dwg", {}).get("open"), d)

    r, d = c.call("export_pdf", list_options=True)
    check("export_pdf list_options", ok(r) and len(d["papers"]) > 10 and d["plot_styles"], d)
    pdf = os.path.join(tmp, "t3_sheets.pdf")
    r, d = c.call("export_pdf", layouts=["*"], path=pdf, paper="A3", plot_style="monochrome.ctb")
    check("export_pdf multi-sheet A3", ok(r) and is_pdf(pdf) and len(d["files"][0]["sheets"]) >= 2 and
          "A3" in d["files"][0]["sheets"][0]["paper"], d)
    r, d = c.call("export_pdf", layouts=["Model"], path=os.path.join(tmp, "t3_model.pdf"))
    check("export_pdf Model = extents, fit", ok(r) and d["files"][0]["sheets"][0]["area"] == "extents" and
          d["files"][0]["sheets"][0]["scale"] == "fit", d)
    sheets_dir = os.path.join(tmp, "sheets")
    os.makedirs(sheets_dir, exist_ok=True)
    r, d = c.call("export_pdf", layouts=["*"], separate_files=True, path=sheets_dir)
    check("export_pdf one file per layout", ok(r) and len(d["files"]) >= 2 and all(is_pdf(f["path"]) for f in d["files"]), d)
    r, d = c.call("export_pdf", layouts=["NO_SUCH_LAYOUT"], path=pdf)
    check("export_pdf rejects unknown layout", r.get("isError"), d)
    r, d = c.call("publish_pdf", path=os.path.join(tmp, "t3_publish.pdf"))
    check("publish_pdf active drawing", ok(r) and is_pdf(os.path.join(tmp, "t3_publish.pdf")) and d["sheet_count"] >= 2, d)
    r, d = c.call("publish_pdf", drawings=[main, old], layouts=["T3*"], path=os.path.join(tmp, "t3_publish_two.pdf"))
    check("publish_pdf two drawings (one closed)", ok(r) and is_pdf(os.path.join(tmp, "t3_publish_two.pdf")) and d["sheet_count"] == 2, d)

    # import a layer back from the copy, then CSV exports
    r, d = c.call("delete_object", kind="layer", names=["T3-SRC"])
    check("delete layer before import", ok(r), d)
    r, d = c.call("import_from_drawing", path=copy, kind="layer", names=["T3-SRC"])
    check("import_from_drawing layer", ok(r) and d["imported"] == ["T3-SRC"], d)
    r, d = c.call("import_from_drawing", path=copy, kind="layer", names=["T3-SRC", "NO_SUCH_LAYER"])
    check("import_from_drawing skips existing", ok(r) and d["skipped_existing"] == ["T3-SRC"] and d.get("not_in_source") == ["NO_SUCH_LAYER"], d)
    r, d = c.call("import_from_drawing", path=copy, kind="layer", names=["T3-SRC"], replace=True)
    check("import_from_drawing replace", ok(r) and d["replaced"] == ["T3-SRC"], d)
    qto = os.path.join(tmp, "t3_qto.csv")
    r, d = c.call("quantity_takeoff", group_by="layer_type", length_factor=0.001, csv_path=qto)
    check("quantity_takeoff CSV", ok(r) and d["total"]["count"] > 0 and os.path.exists(qto) and open(qto, "rb").read(3) == b"\xef\xbb\xbf", d)
    blocks_csv = os.path.join(tmp, "t3_blocks.csv")
    r, d = c.call("extract_block_data", block_names=["T3_ROOM"], csv_path=blocks_csv, delimiter=";")
    check("extract_block_data CSV", ok(r) and os.path.exists(blocks_csv) and "ROOM" in open(blocks_csv, encoding="utf-8-sig").readline(), d)

    r, d = c.call("attach_xref", path=copy, relative_path=True, position=[5000, 0])
    check("attach_xref relative", ok(r) and d["status"] == "loaded" and d["path"].startswith(".\\"), d)
    r, d = c.call("list_named_objects", kind="xref")
    check("list_named_objects xref", ok(r) and d["count"] == 1, d)
    r, d = c.call("manage_xref", names=["t3_copy"], action="unload")
    check("manage_xref unload", ok(r) and d["xrefs"][0]["status"] == "unloaded", d)
    r, d = c.call("manage_xref", names=["t3_copy"], action="reload")
    check("manage_xref reload", ok(r) and d["xrefs"][0]["status"] == "loaded", d)
    r, d = c.call("manage_xref", names=["t3_copy"], action="detach")
    check("manage_xref detach", ok(r), d)

    r, d = c.call("rename_document", new_name="t3_renamed")
    check("rename_document refuses unsaved changes", r.get("isError") and "unsaved" in d, d)
    r, d = c.call("rename_document", new_name="t3_renamed", save_changes=True)
    renamed = os.path.join(tmp, "t3_renamed.dwg")
    check("rename_document open drawing", ok(r) and d["reopened"] and os.path.exists(renamed) and not os.path.exists(main), d)
    r, d = c.call("rename_document", path=old, new_name="t3_old.dwg")
    old = os.path.join(tmp, "t3_old.dwg")
    check("rename_document closed file", ok(r) and os.path.exists(old), d)

    r, d = c.call("new_document", save_as=os.path.join(tmp, "t3_new.dwg"))
    check("new_document save_as", ok(r) and os.path.exists(os.path.join(tmp, "t3_new.dwg")), d)
    r, d = c.call("close_document", document="t3_new.dwg")
    check("close the saved new drawing", ok(r), d)
    r, d = c.call("activate_document", document="t3_renamed.dwg")

    r, d = c.call("delete_drawing_file", path=renamed)
    check("delete_drawing_file refuses an open drawing", r.get("isError"), d)
    r, d = c.call("delete_drawing_file", path=old)
    check("delete_drawing_file to Recycle Bin", ok(r) and d["recycle_bin"] and not os.path.exists(old), d)
    r, d = c.call("delete_drawing_file", path=copy, permanent=True)
    check("delete_drawing_file permanent", ok(r) and not os.path.exists(copy), d)

    r, status = c.call("get_status")
    return ok(r) and status.get("active_document", {}).get("name") == "t3_renamed.dwg"


if __name__ == "__main__":
    sys.exit(main())

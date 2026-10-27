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

import subprocess  # noqa: E402
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

    r, d = c.call("close_document")
    check("close_document refuses unsaved", r.get("isError") and "unsaved" in d, d)
    r, d = c.call("close_document", discard_changes=True)
    check("close_document discard", not r.get("isError"), d)
    r, status = c.call("get_status")
    check("back to the user's drawing (or none)", status.get("active_document", {}).get("name") == user_doc, status.get("active_document"))

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


if __name__ == "__main__":
    sys.exit(main())

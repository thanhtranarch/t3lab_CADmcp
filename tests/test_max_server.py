"""T3LabMax server without 3ds Max: a fake pymxs, the real socket wire, the main-thread pump.

    python -I tests/test_max_server.py

Checks health / tools/list on the socket thread, tools/call only through pump(), writes inside one
named undo step, the main tools on a fake scene, image results, the run_maxscript blocklist.
Uses ports 49920-49922.
"""

import base64
import contextlib
import json
import os
import socket
import sys
import tempfile
import threading
import types
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "src", "T3LabMax"))


# ─── fake pymxs ──────────────────────────────────────────────────────────────

class P3(object):
    def __init__(self, x=0.0, y=0.0, z=0.0):
        self.x, self.y, self.z = float(x), float(y), float(z)


class Color(object):
    def __init__(self, r, g, b):
        self.r, self.g, self.b = r, g, b


class Layer(object):
    def __init__(self, name):
        self.name, self.isHidden, self.isFrozen, self.current = name, False, False, name == "0"

    def addNode(self, node):
        node.layer = self


class Node(object):
    cls = "Box"
    superclass = "GeometryClass"

    def __init__(self):
        self.name = "{}{:03d}".format(self.cls, len(RT.objects) + 1)
        self.position, self.scale, self.rotation = P3(), P3(1, 1, 1), None
        self.isHidden, self.renderable, self.material, self.parent = False, True, None, None
        self.children, self.modifiers, self.userprops = [], [], ""
        self.wirecolor = Color(10, 20, 30)
        self.layer = RT.LayerManager.layers[0]
        RT.objects.append(self)


class Box(Node):
    length = width = height = 10.0


class Sphere(Node):
    cls = "Sphere"
    radius = 5.0


class Material(object):
    def __init__(self, cls):
        self.cls, self.name = cls, ""


class LayerManager(object):
    layers = [Layer("0")]

    @property
    def count(self):
        return len(self.layers)

    def getLayer(self, i):
        return self.layers[i]

    def getLayerFromName(self, name):
        return next((l for l in self.layers if l.name == name), None)

    def newLayerFromName(self, name):
        self.layers.append(Layer(name))
        return self.layers[-1]


class Dib(object):
    width, height, filename = 1920, 1080, ""


class Runtime(object):
    def __init__(self):
        self.objects, self.selection, self.executed = [], [], []
        self.LayerManager = LayerManager()
        self.maxFilePath, self.maxFileName = "C:/scenes/", "tower.max"
        self.units = types.SimpleNamespace(SystemType="millimeters")
        self.renderers = types.SimpleNamespace(current="Arnold")
        self.gw = types.SimpleNamespace(getViewportDib=lambda: Dib())
        self.Box, self.Sphere = Box, Sphere
        self.point3, self.color = P3, Color

    def maxVersion(self):
        return [27000]

    def getNodeByName(self, name):
        return next((n for n in self.objects if n.name == name), None)

    def classOf(self, obj):
        return obj.cls

    def superClassOf(self, obj):
        return obj.superclass

    def matrix3(self, _):
        return None

    def nodeGetBoundingBox(self, node, _):
        p = node.position
        return P3(p.x - 1, p.y - 1, p.z - 1), P3(p.x + 1, p.y + 1, p.z + 1)

    def clearSelection(self):
        self.selection[:] = []

    def selectMore(self, node):
        self.selection.append(node)

    def move(self, node, d):
        node.position = P3(node.position.x + d.x, node.position.y + d.y, node.position.z + d.z)

    def eulerAngles(self, *a):
        return a

    def rotate(self, node, euler):
        node.rotation = euler

    def quatToEuler2(self, q):
        return P3(*q)

    def scale(self, node, f):
        node.scale = P3(node.scale.x * f.x, node.scale.y * f.y, node.scale.z * f.z)

    def getUserPropBuffer(self, node):
        return node.userprops

    def setUserPropBuffer(self, node, text):
        node.userprops = text

    def PhysicalMaterial(self):
        return Material("PhysicalMaterial")

    def StandardMaterial(self):
        return Material("Standardmaterial")

    def delete(self, node):
        self.objects.remove(node)

    def execute(self, code):
        self.executed.append(code)
        return 42

    def redrawViews(self):
        pass

    def bitmap(self, w, h):
        d = Dib()
        d.width, d.height = w, h
        return d

    def copy(self, a, b):
        pass

    def save(self, dib):
        with open(dib.filename, "wb") as f:
            f.write(b"\x89PNG fake")

    def close(self, dib):
        pass


RT = Runtime()
UNDO = []


@contextlib.contextmanager
def undo(on, label):
    UNDO.append(label)
    yield


sys.modules["pymxs"] = types.SimpleNamespace(runtime=RT, undo=undo)
import t3lab_max_mcp as srv  # noqa: E402


# ─── wire ────────────────────────────────────────────────────────────────────

def rpc(method, params=None):
    with socket.create_connection(("127.0.0.1", srv._STATE["port"]), timeout=5) as s:
        s.sendall((json.dumps({"id": 7, "method": method, "params": params}) + "\n").encode())
        return json.loads(s.makefile("rb").readline())


def call(_tool, **args):
    """tools/call from a client thread while this (main) thread pumps."""
    box = {}
    t = threading.Thread(target=lambda: box.update(r=rpc("tools/call", {"name": _tool, "arguments": args})))
    t.start()
    while t.is_alive():
        srv.pump()
        t.join(0.01)
    result = box["r"]["result"]
    text = result["content"][-1]["text"]
    try:
        data = json.loads(text)
    except ValueError:
        data = text
    return result, data


class MaxServerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        srv.start(use_qt=False, port_min=49920, port_max=49922)

    @classmethod
    def tearDownClass(cls):
        srv.stop()

    def setUp(self):
        RT.objects[:] = []
        RT.selection[:] = []
        UNDO[:] = []

    def test_health_and_list_without_pump(self):
        h = rpc("health")["result"]
        self.assertEqual((h["app"], h["max"], h["port"]), ("T3LabMax", "27000", srv._STATE["port"]))
        names = [t["name"] for t in rpc("tools/list")["result"]["tools"]]
        self.assertIn("create_objects", names)
        self.assertIn("error", rpc("nope"))

    def test_create_list_transform(self):
        r, data = call("create_objects", items=[
            {"type": "box", "name": "Slab", "position": [0, 0, 3000], "params": {"height": 200},
             "layer": "Structure", "wirecolor": [255, 0, 0]},
            {"type": "Sphere", "params": {"radius": 50}}])
        self.assertFalse(r.get("isError"), r)
        self.assertEqual([o["name"] for o in data["created"]], ["Slab", "Sphere002"])
        self.assertEqual(UNDO, ["T3Lab MCP: create_objects"])
        self.assertEqual(RT.getNodeByName("Slab").layer.name, "Structure")
        _, data = call("list_objects", detail="count")
        self.assertEqual(data, {"total": 2, "by_class": {"Box": 1, "Sphere": 1}})
        _, data = call("list_objects", layer="structure", detail="bbox")
        self.assertEqual(data["objects"][0]["bbox"], [[-1, -1, 2999], [1, 1, 3001]])
        _, data = call("transform_objects", names=["Slab"], move=[10, 0, 0], scale=2)
        self.assertEqual(data["objects"][0]["position"], [10, 0, 3000])
        self.assertEqual(RT.getNodeByName("Slab").scale.x, 2)
        _, data = call("get_object", name="Slab")
        self.assertEqual((data["wirecolor"], data["layer"]), ([255, 0, 0], "Structure"))

    def test_bad_input_is_error_not_crash(self):
        r, text = call("create_objects", items=[{"type": "Spaceship"}])
        self.assertTrue(r["isError"])
        self.assertIn("not one of", text)
        r, text = call("get_object", name="Ghost")
        self.assertTrue(r["isError"])
        r, _ = call("no_such_tool")
        self.assertTrue(r["isError"])

    def test_properties_material_delete(self):
        call("create_objects", items=[{"type": "Box", "name": "A"}, {"type": "Box", "name": "B"}])
        _, data = call("set_properties", names=["A"], new_name="Wall", hidden=True, user_properties="id=7")
        self.assertEqual(data["objects"][0]["name"], "Wall")
        self.assertEqual(RT.getUserPropBuffer(RT.getNodeByName("Wall")), "id=7")
        _, data = call("assign_material", names=["Wall", "B"], name="Concrete", roughness=0.8)
        self.assertEqual(data, {"material": "Concrete", "objects": 2})
        _, data = call("list_materials")
        self.assertEqual(data["materials"][0]["objects"], 2)
        _, data = call("list_layers")
        self.assertEqual(data["layers"][0]["objects"], 2)
        _, data = call("delete_objects", names=["B"])
        self.assertEqual((data, len(RT.objects)), ({"deleted": 1}, 1))
        self.assertEqual(UNDO[-1], "T3Lab MCP: delete_objects")

    def test_reads_have_no_undo_step(self):
        call("get_status")
        call("list_objects")
        self.assertEqual(UNDO, [])

    def test_maxscript_blocklist(self):
        r, text = call("run_maxscript", code='DOSCommand "del c:\\\\x"')
        self.assertTrue(r["isError"])
        r, text = call("run_maxscript", code='python.Execute "import os"')
        self.assertTrue(r["isError"])
        r, data = call("run_maxscript", code="1 + 41")
        self.assertEqual(data, {"result": "42"})

    def test_screenshot_is_image(self):
        r, data = call("viewport_screenshot", max_width=960)
        self.assertEqual(r["content"][0]["type"], "image")
        self.assertEqual(base64.b64decode(r["content"][0]["data"]), b"\x89PNG fake")
        self.assertEqual((data["width"], data["height"]), (960, 540))

    def test_tool_schemas(self):
        for t in srv.tools_list():
            self.assertEqual(t["inputSchema"]["type"], "object", t["name"])
            self.assertIn("readOnlyHint", t["annotations"])
        ro = {t["name"]: t["annotations"]["readOnlyHint"] for t in srv.tools_list()}
        self.assertTrue(ro["list_objects"])
        self.assertFalse(ro["delete_objects"])


if __name__ == "__main__":
    tempfile.tempdir = tempfile.mkdtemp(prefix="t3labmax_test_")
    unittest.main(verbosity=1)

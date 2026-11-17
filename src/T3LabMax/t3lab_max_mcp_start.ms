-- T3LabMax MCP add-in: start-up script, run by T3LabMax.bundle (PackageContents.xml).
-- Puts this folder on Python's sys.path and starts the server (t3lab_max_mcp.py).
-- MAXScript Listener: T3MCPStatus() / T3MCPStart() / T3MCPStop().
(
    local here = substituteString (getFilenamePath (getThisScriptFilename())) "\\" "/"
    python.Execute ("import sys\nif r'" + here + "' not in sys.path: sys.path.insert(0, r'" + here + "')")
    python.Execute "import t3lab_max_mcp\nt3lab_max_mcp.main()"
)

global T3MCPStatus = fn T3MCPStatus = (python.Execute "import t3lab_max_mcp\nprint(t3lab_max_mcp.status())"; ok)
global T3MCPStart = fn T3MCPStart = (python.Execute "import t3lab_max_mcp\nprint(t3lab_max_mcp.start())"; ok)
global T3MCPStop = fn T3MCPStop = (python.Execute "import t3lab_max_mcp\nprint(t3lab_max_mcp.stop())"; ok)

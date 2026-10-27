<#
.SYNOPSIS
  Build the T3LabCAD AutoCAD add-in, install it as an autoloader bundle, deploy the MCP bridge
  and register it with Claude Desktop.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -AcadDir "C:\Program Files\Autodesk\AutoCAD 2023"
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -ClaudeCode      # also `claude mcp add` (user scope)

  While AutoCAD runs, a new T3LabCAD.Core.dll is picked up with T3MCPRELOAD (or the reload_addin tool).
  The loader DLL is locked by AutoCAD: when it changes, close AutoCAD and run this again.
#>
param(
    [string]$AcadDir = "C:\Program Files\Autodesk\AutoCAD 2024",
    [string]$Python = "",
    [switch]$SkipBuild,
    [switch]$NoClaudeConfig,
    [switch]$ClaudeCode
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$env:PYTHONPATH = ""   # pyRevit's PYTHONPATH breaks other interpreters

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- build
if (-not $SkipBuild) {
    Step "Build (AutoCAD API from $AcadDir)"
    foreach ($project in "T3LabCAD.Core", "T3LabCAD.Loader") {
        dotnet build "$root\src\$project\$project.csproj" -c Release -nologo -v q "-p:AcadDir=$AcadDir"
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }
}

# ---------------------------------------------------------------- bundle
$bundle = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\T3LabCAD.bundle"
$contents = Join-Path $bundle "Contents"
Step "Bundle -> $bundle"
New-Item -ItemType Directory -Force $contents | Out-Null
Copy-Item "$root\bundle\PackageContents.xml" $bundle -Force

function Same($a, $b) {
    (Test-Path $b) -and ((Get-FileHash $a).Hash -eq (Get-FileHash $b).Hash)
}

$restartNeeded = $false
foreach ($file in "T3LabCAD.Core.dll", "T3LabCAD.Core.pdb", "T3LabCAD.Loader.dll", "T3LabCAD.Loader.pdb") {
    $project = $file -replace '\.(dll|pdb)$', ''
    $src = "$root\src\$project\bin\Release\$file"
    $dst = Join-Path $contents $file
    if (-not (Test-Path $src)) { throw "Missing build output $src (run without -SkipBuild)" }
    if (Same $src $dst) { Write-Host "  $file unchanged"; continue }
    try {
        Copy-Item $src $dst -Force
        Write-Host "  $file updated"
    } catch {
        # only the loader is ever locked (Core is loaded from bytes)
        Write-Warning "$file is in use by AutoCAD; close AutoCAD and run install.ps1 again to update it."
        $restartNeeded = $true
    }
}

# ---------------------------------------------------------------- bridge
$data = Join-Path $env:APPDATA "T3LabCAD"
Step "Bridge -> $data\bridge.py"
New-Item -ItemType Directory -Force $data | Out-Null
Copy-Item "$root\bridge\t3lab_cad_bridge.py" "$data\bridge.py" -Force
if (Test-Path "$root\bridge\tools_snapshot.json") { Copy-Item "$root\bridge\tools_snapshot.json" "$data\tools_snapshot.json" -Force }

# ---------------------------------------------------------------- python
if (-not $Python) {
    $candidates = @("$env:LOCALAPPDATA\Microsoft\WindowsApps\python.exe", "py.exe", "python.exe")
    foreach ($candidate in $candidates) {
        try {
            $out = & $candidate -I -c "import sys; print(sys.version_info[0])" 2>$null
            if ($LASTEXITCODE -eq 0 -and "$out".Trim() -eq "3") { $Python = $candidate; break }
        } catch { }
    }
    if (-not $Python) { throw "No working Python 3 found; pass -Python <path to python.exe>" }
}
Write-Host "  python: $Python"

# ---------------------------------------------------------------- Claude
$bridgePath = ("$data\bridge.py" -replace '\\', '/')
if (-not $NoClaudeConfig) {
    Step "Claude Desktop config"
    & $Python -I "$root\scripts\register_mcp.py" $bridgePath $Python
    if ($LASTEXITCODE -ne 0) { throw "register_mcp.py failed" }
}
if ($ClaudeCode) {
    Step "Claude Code (user scope)"
    claude mcp add --scope user t3lab-autocad -- $Python -I $bridgePath
}

Step "Done"
if ($restartNeeded) {
    Write-Host "Restart AutoCAD to load the new loader." -ForegroundColor Yellow
} else {
    Write-Host "AutoCAD loads the bundle at startup (and usually picks up a new bundle within seconds while running)."
    Write-Host "Already loaded? Type T3MCPRELOAD in AutoCAD to load the new Core. Check with T3MCPSTATUS."
}

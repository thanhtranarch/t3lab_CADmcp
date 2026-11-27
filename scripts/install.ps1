<#
.SYNOPSIS
  Build the T3LabCAD AutoCAD add-in for every supported AutoCAD (2022-2027, Civil 3D too),
  install it as an autoloader bundle, deploy the MCP bridge and register it with Claude Desktop.
  Without a .NET SDK, T3Lab's MCP Control installs the same bundle from the GitHub release (Connect).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Series R26       # only AutoCAD 2027
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -ClaudeCode      # also `claude mcp add` (user scope)

  Builds need the .NET 10 SDK (it builds the .NET Framework 4.8 and .NET 8 series too); the AutoCAD
  API comes from Autodesk's NuGet packages, so no AutoCAD install is needed to build.
  While AutoCAD runs, a new T3LabCAD.Core.dll is picked up with T3MCPRELOAD (or the reload_addin tool).
  The loader DLL is locked by AutoCAD: when it changes, close AutoCAD and run this again.
#>
param(
    [string[]]$Series = @("R24", "R25", "R26"),   # R24 = 2022-2024, R25 = 2025-2026, R26 = 2027
    [string]$AcadDir = "",                         # ignored since 1.1.0 (NuGet AutoCAD.NET)
    [string]$Python = "",
    [switch]$SkipBuild,
    [switch]$NoClaudeConfig,
    [switch]$ClaudeCode
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$env:PYTHONPATH = ""   # pyRevit's PYTHONPATH breaks other interpreters

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
if ($AcadDir) { Write-Warning "-AcadDir is no longer needed: the AutoCAD API comes from NuGet." }

# ---------------------------------------------------------------- build
if (-not $SkipBuild) {
    foreach ($s in $Series) {
        Step "Build $s"
        foreach ($project in "T3LabCAD.Core", "T3LabCAD.Loader") {
            dotnet build "$root\src\$project\$project.csproj" -c Release -nologo -v q "-p:AcadSeries=$s"
            if ($LASTEXITCODE -ne 0) { throw "Build failed: $project ($s)" }
        }
    }
}

# ---------------------------------------------------------------- bundle
$bundle = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\T3LabCAD.bundle"
Step "Bundle -> $bundle"
New-Item -ItemType Directory -Force (Join-Path $bundle "Contents") | Out-Null
Copy-Item "$root\bundle\PackageContents.xml" $bundle -Force

function Same($a, $b) {
    (Test-Path $b) -and ((Get-FileHash $a).Hash -eq (Get-FileHash $b).Hash)
}

$restartNeeded = $false
foreach ($s in $Series) {
    $contents = Join-Path $bundle "Contents\$s"
    New-Item -ItemType Directory -Force $contents | Out-Null
    foreach ($file in "T3LabCAD.Core.dll", "T3LabCAD.Core.pdb", "T3LabCAD.Loader.dll", "T3LabCAD.Loader.pdb") {
        $project = $file -replace '\.(dll|pdb)$', ''
        $src = "$root\src\$project\bin\Release\$s\$file"
        $dst = Join-Path $contents $file
        if (-not (Test-Path $src)) { throw "Missing build output $src (run without -SkipBuild)" }
        if (Same $src $dst) { Write-Host "  $s\$file unchanged"; continue }
        try {
            Copy-Item $src $dst -Force
            Write-Host "  $s\$file updated"
        } catch {
            # only the loader is ever locked (Core is loaded from bytes)
            Write-Warning "$s\$file is in use by AutoCAD; close AutoCAD and run install.ps1 again to update it."
            $restartNeeded = $true
        }
    }
}
# Before 1.1.0 the DLLs sat directly in Contents\ (AutoCAD 2021-2024 only).
foreach ($old in "T3LabCAD.Core.dll", "T3LabCAD.Core.pdb", "T3LabCAD.Loader.dll", "T3LabCAD.Loader.pdb") {
    $path = Join-Path $bundle "Contents\$old"
    if (Test-Path $path) { Remove-Item $path -Force -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------- bridge
$data = Join-Path $env:APPDATA "T3LabCAD"
Step "Bridge -> $data\bridge.py"
New-Item -ItemType Directory -Force $data | Out-Null
Copy-Item "$root\bridge\t3lab_cad_bridge.py" "$data\bridge.py" -Force
Copy-Item "$root\scripts\register_mcp.py" "$data\register_mcp.py" -Force   # Connect AI on the ribbon runs it
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
    & $Python -I "$data\register_mcp.py" --bridge $bridgePath --python $Python --clients claude-desktop
    if ($LASTEXITCODE -eq 3) { Write-Warning "Claude Desktop not updated (it is running). Quit it and run: install.ps1 -SkipBuild" }
    elseif ($LASTEXITCODE -ne 0) { throw "register_mcp.py failed" }
}
if ($ClaudeCode) {
    Step "Claude Code (user scope)"
    & $Python -I "$data\register_mcp.py" --bridge $bridgePath --python $Python --clients claude-code
}
Write-Host "  Other AI apps (Codex, Cursor, VS Code, Gemini CLI, Windsurf, Antigravity): Connect AI on the Add-ins tab in AutoCAD."

Step "Done"
if ($restartNeeded) {
    Write-Host "Restart AutoCAD to load the new loader." -ForegroundColor Yellow
} else {
    Write-Host "AutoCAD loads the bundle at startup (and usually picks up a new bundle within seconds while running)."
    Write-Host "Already loaded? Type T3MCPRELOAD in AutoCAD to load the new Core. Check with T3MCPSTATUS."
}

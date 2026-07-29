# VNyan SoftBody Suite installer
# - Lets you pick your VNyan folder (defaults to C:\Program Files\VNyan)
# - Only asks for admin rights if the chosen folder is actually write-protected
# Usage: double-click install.bat (or: powershell -File install.ps1)

param(
    [string]$Target = "",     # VNyan root folder (skip the picker)
    [switch]$Elevated         # internal: set when relaunched with admin rights
)

$ErrorActionPreference = "Stop"
$plugins = @("SquishStudio", "WobbleStudio", "JelloStudio", "SoftBodyStudio")

# ---- locate the plugin payload (release zip layout OR repo layout) ----
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$payload = $null
foreach ($base in @($here, (Join-Path $here "plugins"))) {
    if (Test-Path (Join-Path $base "SquishStudio\SquishStudio.dll")) { $payload = $base; break }
}
if (-not $payload) {
    Write-Host "ERROR: plugin folders not found next to this script." -ForegroundColor Red
    Write-Host "Keep install.ps1/install.bat in the unzipped folder alongside SquishStudio, WobbleStudio, ..."
    Read-Host "Press Enter to exit"
    exit 1
}

# ---- pick the VNyan folder ----
if (-not $Target) {
    $default = "C:\Program Files\VNyan"
    Write-Host ""
    Write-Host "VNyan SoftBody Suite installer" -ForegroundColor Cyan
    Write-Host "------------------------------"
    if (Test-Path (Join-Path $default "VNyan.exe")) {
        Write-Host "Found VNyan at: $default"
        $ans = Read-Host "Install there? [Y] yes / [n] choose another folder"
        if ($ans -eq "" -or $ans -match "^[Yy]") { $Target = $default }
    }
    if (-not $Target) {
        Write-Host "Pick your VNyan folder (the one containing VNyan.exe)..."
        Add-Type -AssemblyName System.Windows.Forms | Out-Null
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        $dlg.Description = "Select your VNyan folder (contains VNyan.exe)"
        if (Test-Path $default) { $dlg.SelectedPath = $default }
        if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
            Write-Host "Cancelled."; exit 1
        }
        $Target = $dlg.SelectedPath
    }
}

# ---- sanity-check the chosen folder ----
if (-not (Test-Path (Join-Path $Target "VNyan.exe"))) {
    Write-Host ""
    Write-Host "WARNING: no VNyan.exe found in '$Target'." -ForegroundColor Yellow
    $ans = Read-Host "Install anyway? [y/N]"
    if ($ans -notmatch "^[Yy]") { Write-Host "Cancelled."; exit 1 }
}
$assemblies = Join-Path $Target "Items\Assemblies"

# ---- warn if VNyan is running ----
if (Get-Process -Name "VNyan" -ErrorAction SilentlyContinue) {
    Write-Host ""
    Write-Host "VNyan is running - it locks plugin files. Please close it." -ForegroundColor Yellow
    Read-Host "Press Enter once VNyan is closed"
}

# ---- elevation only if the target is actually write-protected ----
function Test-Writable([string]$dir) {
    try {
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        $probe = Join-Path $dir ("_probe_" + [Guid]::NewGuid().ToString("N") + ".tmp")
        [IO.File]::WriteAllText($probe, "x")
        Remove-Item $probe -Force
        return $true
    } catch { return $false }
}

if (-not (Test-Writable $assemblies)) {
    if ($Elevated) {
        Write-Host "ERROR: still cannot write to '$assemblies' even with admin rights." -ForegroundColor Red
        Read-Host "Press Enter to exit"
        exit 1
    }
    Write-Host ""
    Write-Host "'$assemblies' is write-protected - requesting admin rights..." -ForegroundColor Yellow
    $script = $MyInvocation.MyCommand.Path
    Start-Process powershell -Verb RunAs -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", "`"$script`"", "-Target", "`"$Target`"", "-Elevated"
    )
    exit 0
}

# ---- install ----
Write-Host ""
$installed = @()
foreach ($p in $plugins) {
    $src = Join-Path $payload $p
    if (-not (Test-Path "$src\$p.dll")) { Write-Host "  skipping $p (not in this package)"; continue }
    $dst = Join-Path $assemblies $p
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item "$src\$p.dll" $dst -Force
    Copy-Item "$src\$p.vnobj" $dst -Force
    $installed += $p
    Write-Host ("  installed " + $p) -ForegroundColor Green
}

Write-Host ""
if ($installed.Count -gt 0) {
    Write-Host ("Done! Installed: " + ($installed -join ", ")) -ForegroundColor Cyan
    Write-Host "Start VNyan, open each studio from the plugins menu, and flip its On toggle."
    Write-Host "Docs: https://github.com/alienware377/VNyan-SoftBody-Suite"
} else {
    Write-Host "Nothing installed." -ForegroundColor Yellow
}
Read-Host "Press Enter to close"

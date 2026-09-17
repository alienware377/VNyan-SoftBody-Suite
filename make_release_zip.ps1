# Build the release zip.
#   .\make_release_zip.ps1 -Version 2.0.1
#
# Entries are written one at a time with FORWARD SLASHES. Both Compress-Archive and
# ZipFile.CreateFromDirectory write backslash separators, which several unzip tools
# treat as part of the filename rather than as a folder — producing one flat pile of
# files called "SoftBodySuite\SoftBodySuite.dll".
param([Parameter(Mandatory=$true)][string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = Join-Path 'C:\Temp\sbs-build' "VNyan-SoftBody-Suite-v$Version.zip"

# published path -> source file
$items = [ordered]@{
  'SoftBodySuite/SoftBodySuite.dll'   = Join-Path $root 'plugins\SoftBodySuite\SoftBodySuite.dll'
  'SoftBodySuite/SoftBodySuite.vnobj' = Join-Path $root 'plugins\SoftBodySuite\SoftBodySuite.vnobj'
  'install.bat'                       = Join-Path $root 'install.bat'
  'install.ps1'                       = Join-Path $root 'install.ps1'
  'README.md'                         = Join-Path $root 'README.md'
  'LICENSE'                           = Join-Path $root 'LICENSE'
  'docs/INSTALL.md'                   = Join-Path $root 'docs\INSTALL.md'
  'docs/USAGE.md'                     = Join-Path $root 'docs\USAGE.md'
  'docs/FAQ.md'                       = Join-Path $root 'docs\FAQ.md'
}

foreach ($k in $items.Keys) { if (-not (Test-Path $items[$k])) { throw "missing: $($items[$k])" } }
if (Test-Path $out) { Remove-Item $out -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fs  = [System.IO.File]::Open($out, [System.IO.FileMode]::CreateNew)
$zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
try {
  foreach ($k in $items.Keys) {
    $entry  = $zip.CreateEntry($k, [System.IO.Compression.CompressionLevel]::Optimal)
    $stream = $entry.Open()
    $bytes  = [System.IO.File]::ReadAllBytes($items[$k])
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Dispose()
    "  {0,-40} {1,8} bytes" -f $k, $bytes.Length
  }
} finally { $zip.Dispose(); $fs.Dispose() }

$f = Get-Item $out
"`nzip: $out  ($([math]::Round($f.Length/1KB,1)) KB)"

# prove the separators survived
$check = [System.IO.Compression.ZipFile]::OpenRead($out)
try {
  $bad = @($check.Entries | Where-Object { $_.FullName -like '*\*' })
  if ($bad.Count -gt 0) { "BACKSLASH ENTRIES: " + ($bad.FullName -join ', '); exit 1 }
  "entries ({0}), all forward-slash:" -f $check.Entries.Count
  $check.Entries | ForEach-Object { "  $($_.FullName)" }
} finally { $check.Dispose() }

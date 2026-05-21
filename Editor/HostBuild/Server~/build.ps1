# Cross-compiles web-host for every supported Unity editor platform and ships each
# as a gzip-compressed ".gz.bytes" TextAsset in ../Bin. Bin is a normal asset folder
# (not a tilde "Bin~"), so the binaries are included in a .unitypackage / Asset Store
# export; the editor decompresses the one it needs into a Library cache at runtime.
# Pure Go + CGO_ENABLED=0 means all targets build from any one host.
#
# Release note: the macOS binaries are unsigned (cross-compiled). The editor ad-hoc
# codesigns and de-quarantines them at runtime so they run locally; proper
# notarization is a future step for wider distribution.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression | Out-Null
$go = (Get-Command go -ErrorAction SilentlyContinue).Source
if (-not $go) { $go = "C:\Program Files\Go\bin\go.exe" }

$src = $PSScriptRoot
$out = Join-Path (Split-Path $src -Parent) "Bin"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("webhost-build-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$env:CGO_ENABLED = "0"

function Compress-Gzip($inPath, $outPath) {
  $bytes = [IO.File]::ReadAllBytes($inPath)
  $fs = [IO.File]::Create($outPath)
  try {
    $gz = New-Object System.IO.Compression.GZipStream($fs, [System.IO.Compression.CompressionLevel]::Optimal)
    try { $gz.Write($bytes, 0, $bytes.Length) } finally { $gz.Dispose() }
  } finally { $fs.Dispose() }
}

$targets = @(
  @{ os = "windows"; arch = "amd64"; ext = ".exe" },
  @{ os = "darwin";  arch = "arm64"; ext = "" },
  @{ os = "darwin";  arch = "amd64"; ext = "" },
  @{ os = "linux";   arch = "amd64"; ext = "" }
)
foreach ($t in $targets) {
  $env:GOOS = $t.os; $env:GOARCH = $t.arch
  $raw = Join-Path $tmp "web-host-$($t.os)-$($t.arch)$($t.ext)"
  Push-Location $src
  & $go build -ldflags "-s -w" -trimpath -o $raw .
  Pop-Location
  if ($LASTEXITCODE -ne 0) { throw "build failed for $($t.os)/$($t.arch)" }
  $bytes = Join-Path $out "web-host-$($t.os)-$($t.arch).gz.bytes"
  Compress-Gzip $raw $bytes
  $rawMB = [math]::Round((Get-Item $raw).Length / 1MB, 2)
  $gzMB  = [math]::Round((Get-Item $bytes).Length / 1MB, 2)
  Write-Host ("packed {0,-22} {1} MB -> {2} MB" -f "$($t.os)/$($t.arch)", $rawMB, $gzMB)
}
Remove-Item Env:GOOS, Env:GOARCH -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
Write-Host "done -> $out"

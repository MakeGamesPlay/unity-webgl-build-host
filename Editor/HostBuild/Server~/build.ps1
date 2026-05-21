# Cross-compiles web-host for every supported Unity editor platform into
# ../Bin~ (a tilde folder Unity ignores). Pure Go + CGO_ENABLED=0 means all
# targets build from any one host - no per-platform build machine needed.
#
# Release note: macOS binaries must be codesigned + notarized (or the editor
# strips the com.apple.quarantine xattr) before shipping. That's a release
# step, not part of this dev build.
$ErrorActionPreference = "Stop"
$go = (Get-Command go -ErrorAction SilentlyContinue).Source
if (-not $go) { $go = "C:\Program Files\Go\bin\go.exe" }

$src = $PSScriptRoot
$out = Join-Path (Split-Path $src -Parent) "Bin~"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$env:CGO_ENABLED = "0"

$targets = @(
  @{ os = "windows"; arch = "amd64"; ext = ".exe" },
  @{ os = "darwin";  arch = "arm64"; ext = "" },
  @{ os = "darwin";  arch = "amd64"; ext = "" },
  @{ os = "linux";   arch = "amd64"; ext = "" }
)
foreach ($t in $targets) {
  $env:GOOS = $t.os; $env:GOARCH = $t.arch
  $bin = Join-Path $out "web-host-$($t.os)-$($t.arch)$($t.ext)"
  Push-Location $src
  & $go build -ldflags "-s -w" -trimpath -o $bin .
  Pop-Location
  if ($LASTEXITCODE -ne 0) { throw "build failed for $($t.os)/$($t.arch)" }
  $sz = [math]::Round((Get-Item $bin).Length / 1MB, 2)
  Write-Host ("built {0,-22} {1} MB" -f "$($t.os)/$($t.arch)", $sz)
}
Remove-Item Env:GOOS, Env:GOARCH -ErrorAction SilentlyContinue
Write-Host "done -> $out"

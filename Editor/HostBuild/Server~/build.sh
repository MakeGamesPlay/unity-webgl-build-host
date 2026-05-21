#!/usr/bin/env bash
# Cross-compiles web-host for every supported Unity editor platform and ships each
# as a gzip-compressed ".gz.bytes" TextAsset in ../Bin. Bin is a normal asset folder
# (not a tilde "Bin~"), so the binaries are included in a .unitypackage / Asset Store
# export; the editor decompresses the one it needs into a Library cache at runtime.
# Pure Go + CGO_ENABLED=0 means all targets build from any one host.
set -euo pipefail
cd "$(dirname "$0")"
out="../Bin"
mkdir -p "$out"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
export CGO_ENABLED=0

build() {
  local os="$1" arch="$2" ext="$3"
  GOOS="$os" GOARCH="$arch" go build -ldflags "-s -w" -trimpath -o "$tmp/web-host-$os-$arch$ext" .
  gzip -c "$tmp/web-host-$os-$arch$ext" > "$out/web-host-$os-$arch.gz.bytes"
  echo "packed $os/$arch"
}

build windows amd64 .exe
build darwin  arm64 ""
build darwin  amd64 ""
build linux   amd64 ""
echo "done -> $out"

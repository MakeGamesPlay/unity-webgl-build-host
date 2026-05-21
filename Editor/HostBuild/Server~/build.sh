#!/usr/bin/env bash
# Cross-compiles web-host for every supported Unity editor platform into
# ../Bin~ (a tilde folder Unity ignores). Pure Go + CGO_ENABLED=0 means all
# targets build from any one host.
#
# Release note: macOS binaries must be codesigned + notarized (or the editor
# strips com.apple.quarantine) before shipping - that's a release step.
set -euo pipefail
cd "$(dirname "$0")"
out="../Bin~"
mkdir -p "$out"
export CGO_ENABLED=0

build() {
  GOOS="$1" GOARCH="$2" go build -ldflags "-s -w" -trimpath -o "$out/web-host-$1-$2$3" .
  echo "built $1/$2"
}

build windows amd64 .exe
build darwin  arm64 ""
build darwin  amd64 ""
build linux   amd64 ""
echo "done -> $out"

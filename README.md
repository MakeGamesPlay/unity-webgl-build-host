# WebGL Build Host

Host a Unity **WebGL / WebGPU** build over your LAN with the *correct* headers, open it
on any device with a QR scan, and watch every device's console **live inside the editor**.
Dependency-free — no Python, Node, or external runtime.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.md)
![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B-black?logo=unity)
![Platforms](https://img.shields.io/badge/editor-Windows%20%7C%20macOS%20%7C%20Linux-informational)
[![Release](https://img.shields.io/github/v/release/MakeGamesPlay/unity-webgl-build-host?include_prereleases&sort=semver)](https://github.com/MakeGamesPlay/unity-webgl-build-host/releases)

<!-- TODO (highest-impact): drop a screenshot of the window (config + QR + tabbed device
     console) and ideally a short GIF of a phone connecting and logs streaming, then
     reference it here, e.g.  ![WebGL Build Host](Documentation~/screenshot.png) -->

## Why

`python -m http.server` and most static servers get two things wrong for Unity Web builds:

- they don't send **`Content-Encoding`** for Unity's pre-compressed `.br` / `.gz` files, so the
  browser falls back to a slow JavaScript decompressor;
- they don't send **`Content-Type: application/wasm`**, so there's no streaming WebAssembly compile.

WebGL Build Host fixes both, adds **COOP/COEP** for `SharedArrayBuffer`, serves your LAN over
self-signed **HTTPS** (a secure context — required for camera, WebXR, and threads), and makes
phone testing one scan away.

## Features

- **Correct headers, automatically** — `Content-Encoding` (br/gz), `application/wasm`, COOP/COEP.
- **Self-signed HTTPS on your LAN** — a secure-context URL any device on the same Wi-Fi can open.
- **Optional Cloudflare quick-tunnel** — a public HTTPS URL to share off-network.
- **Scan-to-open QR** for the best phone-reachable URL.
- **Live per-device console** — every connected browser streams its `console` output and uncaught
  errors into a tabbed, colorized, searchable, level-filterable view (real frame numbers, collapse
  duplicates, multi-select copy), with per-device **Reload**, **Identify**, and **Copy bug report**.
- **Survives recompiles** — the server runs as an independent process; the window re-discovers it
  across domain reloads and editor restarts.
- **Dependency-free** — one tiny native server per editor OS (written in Go, no CGO). No Python, no Node.

## Requirements

- Unity **2022.3 LTS** or newer (Unity 6 "Web" platform — WebGL & WebGPU — supported).
- Editor OS: **Windows**, **macOS** (Apple Silicon + Intel), or **Linux** — a prebuilt server ships for each.
- *(Optional)* [`cloudflared`](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/)
  on your `PATH` for the public quick-tunnel.

## Install

### Option A — UPM Git URL *(recommended)*

In Unity: **Window ▸ Package Manager ▸ ＋ ▸ Add package from git URL…** and paste:

```
https://github.com/MakeGamesPlay/unity-webgl-build-host.git
```

Or add it to `Packages/manifest.json`:

```json
"com.makegamesplay.webbuildhost": "https://github.com/MakeGamesPlay/unity-webgl-build-host.git"
```

### Option B — `.unitypackage`

Download the latest `.unitypackage` from the [Releases](https://github.com/MakeGamesPlay/unity-webgl-build-host/releases)
page and drag it into your project.

### Option C — clone into `Packages/`

```bash
git clone https://github.com/MakeGamesPlay/unity-webgl-build-host.git Packages/unity-webgl-build-host
```

## Usage

1. **Tools ▸ WebGL Build Host**.
2. Pick your Web build output folder.
3. **Start** — then scan the QR or open a listed URL on your phone.
4. Each connected device gets its own console tab; use **Reload** / **Identify** / **Copy bug report** as needed.
5. **Stop** when you're done (the server also stops on editor exit if you ask it to).

## Building the native server from source

Prebuilt binaries live in `Editor/HostBuild/Bin~/`. To rebuild them yourself (Go 1.21+):

- **Windows:** `Editor/HostBuild/Server~/build.ps1` (run with `-ExecutionPolicy Bypass`)
- **macOS / Linux:** `Editor/HostBuild/Server~/build.sh`

One host cross-compiles every platform (`CGO_ENABLED=0`, `-trimpath`, `-ldflags "-s -w"`).

## Troubleshooting

- **HTTPS warning on the device** — it's a self-signed dev certificate; tap **Advanced ▸ Proceed**.
  HTTPS is required for camera, WebXR, and `SharedArrayBuffer`.
- **No public URL appears** — install `cloudflared` and make sure it's on your `PATH`, or just
  untick the tunnel option and use the LAN URL.
- **Browser can't reach the LAN URL** — confirm the phone and editor are on the same network and
  that your OS firewall allows the chosen port.

## License

[MIT](LICENSE.md) © MakeGamesPlay

---

Made by [MakeGamesPlay](https://github.com/MakeGamesPlay). **Building AR for the web?**
Check out **WebAR Image Tracker** on the [Unity Asset Store](https://assetstore.unity.com/publishers/MakeGamesPlay).

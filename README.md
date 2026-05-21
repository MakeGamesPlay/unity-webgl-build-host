# WebGL Build Host

Run your Unity WebGL or WebGPU build on any device on your network in a couple of
clicks, and read each device's console live inside the Editor. No Python, Node, or
other runtime required.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.md)
![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B-black?logo=unity)
![Platforms](https://img.shields.io/badge/editor-Windows%20%7C%20macOS%20%7C%20Linux-informational)
[![Release](https://img.shields.io/github/v/release/MakeGamesPlay/unity-webgl-build-host?include_prereleases&sort=semver)](https://github.com/MakeGamesPlay/unity-webgl-build-host/releases)

<!-- TODO (highest-impact): add a screenshot of the window (config + QR + device console)
     and ideally a short GIF of a phone connecting and logs streaming, then reference it
     here, e.g.  ![WebGL Build Host](Documentation~/screenshot.png) -->

## Overview

Point WebGL Build Host at your build folder and press Start. It serves the build from
the Editor, shows a QR code and copyable URLs, and gives every browser that opens it a
tab in a built-in console so you can watch what happens on the actual device.

Use it to:

- Test a WebGL or WebGPU build on real phones and tablets over Wi-Fi.
- See `console.log` output and uncaught errors from each device as they happen.
- Share a temporary public link with a teammate or client for quick feedback.
- Reproduce device-specific bugs and copy a ready-made bug report.

## Features

- **Open on any device in seconds.** A QR code and copyable URLs point straight to the
  build on your local network.
- **Live per-device console.** Every connected browser gets its own tab streaming its
  console output and uncaught errors, with colour-coded levels, search, level filters,
  frame numbers, duplicate collapsing, and multi-select copy. Each device has its own
  Reload, Identify, and Copy bug report actions.
- **HTTPS on your local network.** A self-signed certificate gives every device a secure
  context, which browsers require for camera, microphone, WebXR, and multithreading.
- **Optional public link.** Serve the build through a Cloudflare quick tunnel to reach
  testers who are not on your network. See [Public link](#public-link-cloudflared) below.
- **Loads Unity builds correctly.** Sends the headers Unity's Web builds need (compressed
  `.br`/`.gz` data, `application/wasm`, and COOP/COEP), so the build streams in quickly
  and `SharedArrayBuffer` is available.
- **Stays up while you work.** The server runs as its own process, so it keeps serving
  across script recompiles, new builds, and Editor restarts. The window reconnects to it
  on its own.
- **No dependencies.** A single small native server is bundled for each Editor OS.

## Requirements

- Unity 2022.3 LTS or newer. Covers the Unity 6 "Web" platform (WebGL and WebGPU).
- Editor OS: Windows, macOS (Apple Silicon or Intel), or Linux. A prebuilt server is
  included for each.
- Optional: `cloudflared`, only if you want a public link. See below.

## Install

### UPM Git URL (recommended)

In Unity, open **Window > Package Manager > + > Add package from git URL** and paste:

```
https://github.com/MakeGamesPlay/unity-webgl-build-host.git
```

Or add it to `Packages/manifest.json`:

```json
"com.makegamesplay.webbuildhost": "https://github.com/MakeGamesPlay/unity-webgl-build-host.git"
```

Append `#v1.0.0` to pin a specific version.

### Clone into Packages/

```bash
git clone https://github.com/MakeGamesPlay/unity-webgl-build-host.git Packages/unity-webgl-build-host
```

A drag-in `.unitypackage` will be added with the Asset Store release.

## Quick start

1. Open **Tools > WebGL Build Host**.
2. Choose your Web build output folder.
3. Press **Start**.
4. Scan the QR code with your phone, or open one of the listed URLs.
5. Each device shows up as a console tab. Use Reload, Identify, or Copy bug report as needed.
6. Press **Stop** when you are done.

## Public link (cloudflared)

By default the build is reachable only on your local network. Enable the public link
option to serve it through a Cloudflare quick tunnel, which gives you a temporary public
HTTPS URL. This helps when:

- the tester is not on your Wi-Fi, such as a remote colleague or a client;
- your network blocks device-to-device traffic, which is common on guest and corporate
  Wi-Fi;
- you want to open the build on a phone using mobile data.

Quick tunnels use Cloudflare's free `cloudflared` tool and need no Cloudflare account or
login. The window checks whether `cloudflared` is installed and, if it is not, shows the
install command for your platform and a download link. To install it yourself:

| Platform | Command |
| --- | --- |
| Windows | `winget install --id Cloudflare.cloudflared` |
| macOS | `brew install cloudflared` |
| Linux | Use Cloudflare's apt/rpm repo, or download a binary (link below) |

Downloads and full instructions: https://github.com/cloudflare/cloudflared/releases

## Building the native server from source

Prebuilt servers live in `Editor/HostBuild/Bin~/`. To rebuild them (Go 1.21 or newer):

- Windows: `Editor/HostBuild/Server~/build.ps1` (run with `-ExecutionPolicy Bypass`)
- macOS or Linux: `Editor/HostBuild/Server~/build.sh`

One machine cross-compiles every platform.

## Troubleshooting

- **Certificate warning on the device.** The HTTPS certificate is self-signed for local
  use. Tap Advanced, then Proceed. HTTPS is what lets the page use the camera, WebXR, and
  `SharedArrayBuffer`.
- **No public link appears.** Install `cloudflared` (see above) and confirm it is on your
  `PATH`, or just use the LAN URL.
- **A device cannot reach the LAN URL.** Make sure the device and the Editor are on the
  same network and that your firewall allows the port.

## License

MIT. See [LICENSE.md](LICENSE.md).

---

Made by [MakeGamesPlay](https://github.com/MakeGamesPlay). Building AR for the web? Take a
look at WebAR Image Tracker on the [Unity Asset Store](https://assetstore.unity.com/publishers/MakeGamesPlay).

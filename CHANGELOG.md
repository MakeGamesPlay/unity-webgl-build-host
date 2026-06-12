# Changelog

## Unreleased
- UPM manifest compliance: added `unityRelease` and the author URL; author name
  matches the Asset Store publisher.

## 1.2.0
- **Tunnel health monitoring.** The host probes the public quick-tunnel URL through a new `/__webhost/healthz` endpoint every 15s and marks it offline after two consecutive failures (or immediately when cloudflared exits). Catches the common case where cloudflared keeps running after the machine sleeps or the network changes but the quick tunnel is silently dead - the editor now shows the tunnel as "offline - Stop & Start to reconnect" instead of a stale "active" URL.
- Branding/CTA links updated to the published Asset Store listings (WebGL Build Host, WebAR Image Tracker) and the GitHub repo.

## 1.1.0
- Dev-mode no-cache: when the active build is a Development Build, files are served with `Cache-Control: no-store` so a device reload always picks up the latest build with no manual cache clear. Release builds keep normal caching.
- Device-console **Clear** now wipes the server-side ring buffer and resets the sequence (previously it cleared only the editor view) - storage is actually freed and the editor resyncs from a clean slate, so the session keeps receiving new logs.
- Closed device tabs stay closed across domain reloads / recompiles (persisted in SessionState) instead of old sessions re-appearing on the next poll.
- New **Save** button (next to Copy) writes the selected tab's full log to a text file.

## 1.0.0
- Initial release.
- Dependency-free local host for Unity Web builds: correct Content-Encoding / wasm / COOP+COEP headers, self-signed HTTPS LAN URL, optional Cloudflare quick-tunnel, scan-to-open QR, self-healing port selection.
- Live per-device console: tabs with live/stale dots, level filters with counts, search, collapse duplicates, frame numbers, multi-select copy, per-device reload / identify / Markdown bug report.
- UI Toolkit window; server runs detached and survives domain reloads / editor restarts.

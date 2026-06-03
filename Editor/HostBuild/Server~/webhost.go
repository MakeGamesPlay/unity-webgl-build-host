package main

import (
	"bytes"
	_ "embed"
	"net/http"
	"strings"
)

// devlogJS is the client shim, embedded at build time so the binary is fully
// self-contained (no sidecar files to ship or locate at runtime).
//
//go:embed devlog.js
var devlogJS []byte

// webPrefix is the reserved namespace we inject into served builds. Build files
// never live under this path, so there's no collision with user content.
const webPrefix = "/__webhost/"

var shimTag = []byte(`<script src="/__webhost/devlog.js"></script>`)

// handleWebHost serves the reserved /__webhost/* routes: the devlog shim and
// the log WebSocket. (The control endpoint runs on a separate loopback port.)
// Returns true if it handled the request.
func handleWebHost(w http.ResponseWriter, r *http.Request) bool {
	if !strings.HasPrefix(r.URL.Path, webPrefix) {
		return false
	}
	switch r.URL.Path {
	case webPrefix + "devlog.js":
		w.Header().Set("Content-Type", "application/javascript")
		w.Header().Set("Cache-Control", "no-store")
		_, _ = w.Write(devlogJS)
		return true
	case webPrefix + "logs":
		logHub.serveWS(w, r)
		return true
	case webPrefix + "healthz":
		// Reachable over the tunnel/LAN: the tunnel monitor probes this to tell
		// a live-but-dead quick tunnel (common after the machine sleeps) from a
		// healthy one. Any response proves the request reached this origin.
		w.Header().Set("Content-Type", "text/plain")
		w.Header().Set("Cache-Control", "no-store")
		_, _ = w.Write([]byte("ok"))
		return true
	}
	http.NotFound(w, r)
	return true
}

// isHTML reports whether the path is an (uncompressed) HTML document - the only
// thing we rewrite to inject the shim.
func isHTML(full string) bool {
	l := strings.ToLower(full)
	return strings.HasSuffix(l, ".html") || strings.HasSuffix(l, ".htm")
}

// injectShim inserts the devlog <script> immediately after the opening <head>
// tag so it runs before any build script and can wrap console first. Falls back
// to prepending if there's no <head>.
func injectShim(html []byte) []byte {
	lower := bytes.ToLower(html)
	if idx := bytes.Index(lower, []byte("<head")); idx >= 0 {
		if gt := bytes.IndexByte(lower[idx:], '>'); gt >= 0 {
			pos := idx + gt + 1
			out := make([]byte, 0, len(html)+len(shimTag))
			out = append(out, html[:pos]...)
			out = append(out, shimTag...)
			out = append(out, html[pos:]...)
			return out
		}
	}
	return append(append([]byte{}, shimTag...), html...)
}

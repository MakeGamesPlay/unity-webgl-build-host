// web-host is a tiny, dependency-free static file server for Unity Web (WebGL)
// builds. It exists because `python -m http.server` (and most generic static
// servers) get two things wrong that make Unity WebGL slow or broken:
//
//  1. They don't send Content-Encoding for Unity's pre-compressed .br / .gz
//     build files, so the browser can't decompress natively and Unity falls
//     back to a slow JavaScript decompressor (tens of seconds on mobile).
//  2. They don't send Content-Type: application/wasm, so the browser can't
//     use streaming WebAssembly compilation.
//
// It also sets COOP/COEP so SharedArrayBuffer (threaded) builds work.
//
// This file is Phase 1a: static serving + correct headers + a path-traversal
// guard + self-healing port selection. Later phases add HTTPS (self-signed),
// a device-log WebSocket, an editor control endpoint, a status file, and an
// optional Cloudflare tunnel.
package main

import (
	"crypto/tls"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"path"
	"path/filepath"
	"strings"
	"time"
)

func main() {
	root := flag.String("root", ".", "directory to serve (the Unity WebGL build folder)")
	port := flag.Int("port", 8000, "preferred HTTP port (walks up to the next free one if busy)")
	httpsPort := flag.Int("https-port", 8443, "preferred HTTPS port (walks up if busy; self-signed)")
	controlPort := flag.Int("control-port", 8790, "loopback control port for the editor (walks up if busy)")
	host := flag.String("host", "127.0.0.1", "interface to bind (127.0.0.1 = localhost only; 0.0.0.0 = LAN)")
	lan := flag.Bool("lan", false, "bind 0.0.0.0 so phones on the same network can reach it (shortcut for --host 0.0.0.0)")
	noTunnel := flag.Bool("no-tunnel", false, "do not start a Cloudflare quick tunnel")
	cloudflaredPath := flag.String("cloudflared-path", "", "explicit path to the cloudflared binary")
	statusFile := flag.String("status-file", "", "path to write the JSON status file the editor polls")
	heartbeatFile := flag.String("heartbeat-file", "", "path to the editor heartbeat file; the server self-terminates if it goes stale (the editor has been closed) so a forgotten host doesn't run forever")
	logFile := flag.String("log-file", "", "tee all output to this file (for detached launches)")
	verbose := flag.Bool("verbose", false, "also log every device console line to the server log (the editor's device console always shows them)")
	noCache := flag.Bool("no-cache", false, "send Cache-Control: no-store on every served file (development builds — a plain reload always picks up a fresh build, no hard cache-clear needed; release builds omit this so normal browser caching applies)")
	flag.Parse()

	// Detached launches have no console, so tee output to the log file the
	// editor tails. Done first so even early failures are captured.
	if *logFile != "" {
		if f, err := os.OpenFile(*logFile, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o644); err == nil {
			log.SetOutput(f)
		}
	}
	verboseDevLog = *verbose
	if *lan {
		*host = "0.0.0.0"
	}
	lanActive := *host == "0.0.0.0"

	absRoot, err := filepath.Abs(*root)
	if err != nil {
		log.Fatalf("[serve] bad root %q: %v", *root, err)
	}
	if fi, err := os.Stat(absRoot); err != nil || !fi.IsDir() {
		log.Fatalf("[serve] root is not a directory: %s", absRoot)
	}

	handler := newFileHandler(absRoot, *noCache)
	if *noCache {
		log.Printf("[serve] no-cache mode: Cache-Control: no-store on all files (development build)")
	}
	log.Printf("[serve] serving %s", absRoot)
	lanIP := primaryLANIP()

	var httpBound, httpsBound, ctrlBound int

	// HTTP — also the origin a Cloudflare tunnel connects to.
	if httpLn, b, err := listenFromPort(*host, *port, 20); err != nil {
		log.Printf("[serve] HTTP disabled: no free port in %d..%d: %v", *port, *port+19, err)
	} else {
		httpBound = b
		if b != *port {
			log.Printf("[serve] HTTP port %d was busy; bound %d instead", *port, b)
		}
		log.Printf("[serve] http://localhost:%d", b)
		if lanActive && lanIP != "" {
			log.Printf("[serve] http://%s:%d  (LAN)", lanIP, b)
		}
		go func() {
			srv := &http.Server{Handler: handler}
			if err := srv.Serve(httpLn); err != nil && err != http.ErrServerClosed {
				log.Printf("[serve] HTTP server stopped: %v", err)
			}
		}()
	}

	// HTTPS with a self-signed cert — LAN secure-context testing (camera/WebXR/
	// SharedArrayBuffer). Tap through the warning once per device.
	if cert, err := generateSelfSigned(serverHosts()); err != nil {
		log.Printf("[serve] HTTPS disabled: cert generation failed: %v", err)
	} else if httpsLn, b, err := listenFromPort(*host, *httpsPort, 20); err != nil {
		log.Printf("[serve] HTTPS disabled: no free port in %d..%d: %v", *httpsPort, *httpsPort+19, err)
	} else {
		httpsBound = b
		if b != *httpsPort {
			log.Printf("[serve] HTTPS port %d was busy; bound %d instead", *httpsPort, b)
		}
		log.Printf("[serve] https://localhost:%d  (self-signed)", b)
		if lanActive && lanIP != "" {
			log.Printf("[serve] https://%s:%d  (LAN, self-signed)", lanIP, b)
		}
		go func() {
			srv := &http.Server{
				Handler:   handler,
				TLSConfig: &tls.Config{Certificates: []tls.Certificate{cert}},
			}
			if err := srv.ServeTLS(httpsLn, "", ""); err != nil && err != http.ErrServerClosed {
				log.Printf("[serve] HTTPS server stopped: %v", err)
			}
		}()
	}

	// Control endpoint - loopback ONLY (never LAN/tunnel). Bound to 127.0.0.1
	// regardless of --host.
	if ctrlLn, b, err := listenFromPort("127.0.0.1", *controlPort, 50); err != nil {
		log.Printf("[serve] control endpoint disabled: %v", err)
	} else {
		ctrlBound = b
		log.Printf("[serve] control http://127.0.0.1:%d  (loopback, editor only)", b)
		go func() {
			srv := &http.Server{Handler: controlHandler()}
			if err := srv.Serve(ctrlLn); err != nil && err != http.ErrServerClosed {
				log.Printf("[serve] control server stopped: %v", err)
			}
		}()
	}

	// Publish status (single source of truth the editor polls; survives reloads).
	st := status{Pid: os.Getpid(), HTTPPort: httpBound, HTTPSPort: httpsBound, ControlPort: ctrlBound}
	if httpBound != 0 {
		st.LocalURL = fmt.Sprintf("http://localhost:%d", httpBound)
		if lanActive && lanIP != "" {
			st.LanURL = fmt.Sprintf("http://%s:%d", lanIP, httpBound)
		}
	}
	if httpsBound != 0 {
		st.LocalHTTPSURL = fmt.Sprintf("https://localhost:%d", httpsBound)
		if lanActive && lanIP != "" {
			st.LanHTTPSURL = fmt.Sprintf("https://%s:%d", lanIP, httpsBound)
		}
	}
	stat.init(*statusFile, st)

	// Cloudflare quick tunnel (optional) — off-network HTTPS with a trusted
	// public cert. Connects to the local HTTP origin; resolves its URL async
	// and publishes it to the status file.
	if !*noTunnel && httpBound != 0 {
		if exe := findCloudflared(*cloudflaredPath); exe != "" {
			startTunnel(exe, httpBound)
		} else {
			log.Printf("[serve] cloudflared not found — tunnel skipped (LAN + localhost still work)")
		}
	}

	// Self-terminate if the editor stops touching the heartbeat file (Unity
	// closed). The detached server otherwise outlives the editor indefinitely.
	if *heartbeatFile != "" {
		go watchHeartbeat(*heartbeatFile, 10*time.Minute)
	}

	log.Printf("[serve] Ctrl-C to stop")
	select {} // block forever; the listeners run in their own goroutines
}

// watchHeartbeat self-terminates the server when the editor heartbeat file goes
// stale. The editor touches it every ~30s while Unity is open; once it stops for
// longer than maxIdle the editor is gone, so we stop serving (and kill the
// tunnel) rather than run forever. A full maxIdle grace from startup covers the
// brief gap before the first heartbeat lands.
func watchHeartbeat(path string, maxIdle time.Duration) {
	last := time.Now()
	t := time.NewTicker(30 * time.Second)
	defer t.Stop()
	for range t.C {
		if fi, err := os.Stat(path); err == nil && fi.ModTime().After(last) {
			last = fi.ModTime()
		}
		if time.Since(last) <= maxIdle {
			continue
		}
		log.Printf("[serve] editor heartbeat stale for >%v - Unity appears closed; shutting down.", maxIdle)
		if pid := stat.cloudflaredPid(); pid != 0 {
			if p, err := os.FindProcess(pid); err == nil {
				_ = p.Kill()
				log.Printf("[serve] stopped cloudflared (pid %d)", pid)
			}
		}
		os.Exit(0)
	}
}

// listenFromPort tries start, start+1, ... until one binds or attempts run
// out. Mirrors the self-healing behaviour that stopped the editor crashing on
// "address already in use": an orphaned previous run can hold the requested
// port, so we just take the next free one and report it.
func listenFromPort(host string, start, attempts int) (net.Listener, int, error) {
	var lastErr error
	for i := 0; i < attempts; i++ {
		p := start + i
		if p < 1 || p > 65535 {
			continue
		}
		ln, err := net.Listen("tcp", fmt.Sprintf("%s:%d", host, p))
		if err == nil {
			return ln, p, nil
		}
		lastErr = err
	}
	return nil, 0, lastErr
}

func newFileHandler(root string, noCache bool) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// Reserved /__webhost/* namespace (devlog shim + log WebSocket).
		if handleWebHost(w, r) {
			return
		}

		full, ok := safePath(root, r.URL.Path)
		if !ok {
			http.Error(w, "403 forbidden", http.StatusForbidden)
			return
		}
		// A directory (including "/") resolves to its index.html.
		if fi, err := os.Stat(full); err == nil && fi.IsDir() {
			full = filepath.Join(full, "index.html")
		}
		f, err := os.Open(full)
		if err != nil {
			http.Error(w, "404 not found", http.StatusNotFound)
			return
		}
		defer f.Close()
		fi, err := f.Stat()
		if err != nil || fi.IsDir() {
			http.Error(w, "404 not found", http.StatusNotFound)
			return
		}

		// HTML documents are rewritten to inject the devlog shim so every served
		// build reports its console back with zero project changes. Only
		// uncompressed HTML (Unity never emits a compressed index.html).
		if isHTML(full) {
			data, err := io.ReadAll(f)
			if err != nil {
				http.Error(w, "500 read error", http.StatusInternalServerError)
				return
			}
			data = injectShim(data)
			h := w.Header()
			h.Set("Content-Type", "text/html; charset=utf-8")
			h.Set("Cross-Origin-Opener-Policy", "same-origin")
			h.Set("Cross-Origin-Embedder-Policy", "credentialless")
			h.Set("Cache-Control", "no-store")
			_, _ = w.Write(data)
			return
		}

		applyHeaders(w.Header(), full)
		// Development builds: forbid caching of every served file so a plain
		// reload always fetches the fresh build. Unity doesn't content-hash its
		// WebGL output (.data/.wasm/.framework.js keep stable names) nor the
		// template files, so without this the browser heuristic-caches them and
		// serves stale assets after a rebuild. Release builds skip this and let
		// ServeContent's Last-Modified revalidation drive normal caching.
		if noCache {
			w.Header().Set("Cache-Control", "no-store")
		}
		// ServeContent gives us Range requests, If-Modified-Since and a correct
		// Content-Length for free, and respects the Content-Type we already set.
		http.ServeContent(w, r, "", fi.ModTime(), f)
	})
}

// safePath maps a URL path onto a filesystem path under root and refuses any
// path that would escape root (e.g. ../../etc/passwd). path.Clean collapses
// the dot segments; the prefix check is the actual containment guarantee.
func safePath(root, urlPath string) (string, bool) {
	if !strings.HasPrefix(urlPath, "/") {
		urlPath = "/" + urlPath
	}
	clean := path.Clean(urlPath)
	rel := strings.TrimPrefix(clean, "/")
	full := filepath.Join(root, filepath.FromSlash(rel))

	rootWithSep := root
	if !strings.HasSuffix(rootWithSep, string(os.PathSeparator)) {
		rootWithSep += string(os.PathSeparator)
	}
	if full != root && !strings.HasPrefix(full, rootWithSep) {
		return "", false
	}
	return full, true
}

// applyHeaders sets the Content-Encoding / Content-Type that Unity WebGL needs
// (looking through a .br/.gz suffix to the real type) plus cross-origin
// isolation headers for SharedArrayBuffer builds.
func applyHeaders(h http.Header, full string) {
	base := full
	switch {
	case strings.HasSuffix(base, ".br"):
		h.Set("Content-Encoding", "br")
		base = strings.TrimSuffix(base, ".br")
	case strings.HasSuffix(base, ".gz"):
		h.Set("Content-Encoding", "gzip")
		base = strings.TrimSuffix(base, ".gz")
	}

	ct := ""
	switch {
	case strings.HasSuffix(base, ".wasm"):
		ct = "application/wasm"
	case strings.HasSuffix(base, ".js"):
		ct = "application/javascript"
	case strings.HasSuffix(base, ".symbols.json"):
		ct = "application/json"
	case strings.HasSuffix(base, ".json"):
		ct = "application/json"
	case strings.HasSuffix(base, ".data"):
		ct = "application/octet-stream"
	case strings.HasSuffix(base, ".html"), strings.HasSuffix(base, ".htm"):
		ct = "text/html; charset=utf-8"
	case strings.HasSuffix(base, ".css"):
		ct = "text/css; charset=utf-8"
	}
	if ct != "" {
		h.Set("Content-Type", ct)
	}
	h.Set("Cross-Origin-Opener-Policy", "same-origin")
	h.Set("Cross-Origin-Embedder-Policy", "credentialless")
}

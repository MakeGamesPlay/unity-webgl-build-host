package main

import (
	"encoding/json"
	"net/http"
	"sort"
	"strconv"
	"time"
)

// The control endpoint is the editor's pull channel. It runs on a loopback-only
// listener (never the LAN/tunnel) so device data - and later, control actions
// like "reload all devices" - are reachable only from this machine. The editor
// polls it; being pull-based, it survives editor domain reloads with no socket
// to re-establish.

// deviceInfo is the per-device summary the editor renders as a tab (metadata +
// counts + liveness). Log bodies are fetched separately via /__webhost/log.
type deviceInfo struct {
	ID        string  `json:"id"`
	Label     string  `json:"label"`
	UA        string  `json:"ua"`
	GPU       string  `json:"gpu"`
	IP        string  `json:"ip"`
	W         int     `json:"w"`
	H         int     `json:"h"`
	DPR       float64 `json:"dpr"`
	URL       string  `json:"url"`
	FirstSeen int64   `json:"firstSeen"` // unix ms
	LastSeen  int64   `json:"lastSeen"`  // unix ms
	Live      bool    `json:"live"`
	Count     int     `json:"count"`  // buffered line count
	MaxSeq    int64   `json:"maxSeq"` // latest seq (editor cursor hint)
	Errors    int     `json:"errors"`
	Warns     int     `json:"warns"`
}

func controlHandler() http.Handler {
	mux := http.NewServeMux()

	// Device list + metadata + counts (drives the tab strip, dots, badges).
	mux.HandleFunc(webPrefix+"devices", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, map[string]any{"devices": logHub.snapshot()})
	})

	// Log lines for one device with seq > since (the active tab pulls this).
	// "oldest" lets the editor detect a ring-buffer gap (since < oldest-1).
	mux.HandleFunc(webPrefix+"log", func(w http.ResponseWriter, r *http.Request) {
		id := r.URL.Query().Get("id")
		since := atoi64(r.URL.Query().Get("since"))
		limit := atoi(r.URL.Query().Get("limit"))
		d := logHub.lookup(id)
		if d == nil {
			writeJSON(w, map[string]any{"id": id, "lines": []logLine{}, "oldest": int64(0)})
			return
		}
		lines, oldest := d.linesSince(since, limit)
		writeJSON(w, map[string]any{"id": id, "lines": lines, "oldest": oldest})
	})

	mux.HandleFunc(webPrefix+"healthz", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/plain")
		_, _ = w.Write([]byte("ok"))
	})

	// Push a reload to one device (?id=) or every device (no id).
	mux.HandleFunc(webPrefix+"reload", func(w http.ResponseWriter, r *http.Request) {
		logHub.broadcast(`{"t":"reload"}`, r.URL.Query().Get("id"))
		w.Header().Set("Content-Type", "text/plain")
		_, _ = w.Write([]byte("ok"))
	})

	// Flash an identify overlay on one device (?id=) or every device.
	mux.HandleFunc(webPrefix+"identify", func(w http.ResponseWriter, r *http.Request) {
		logHub.broadcast(`{"t":"identify"}`, r.URL.Query().Get("id"))
		w.Header().Set("Content-Type", "text/plain")
		_, _ = w.Write([]byte("ok"))
	})

	return mux
}

func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	_ = json.NewEncoder(w).Encode(v)
}

func atoi64(s string) int64 { n, _ := strconv.ParseInt(s, 10, 64); return n }
func atoi(s string) int     { n, _ := strconv.Atoi(s); return n }

// ─── Hub queries ───────────────────────────────────────────────────

func (h *hub) lookup(id string) *device {
	h.mu.Lock()
	defer h.mu.Unlock()
	return h.devices[id]
}

func (h *hub) snapshot() []deviceInfo {
	h.mu.Lock()
	devs := make([]*device, 0, len(h.devices))
	for _, d := range h.devices {
		devs = append(devs, d)
	}
	h.mu.Unlock()

	out := make([]deviceInfo, 0, len(devs))
	for _, d := range devs {
		d.mu.Lock()
		errs, warns := 0, 0
		for _, ln := range d.lines {
			switch ln.Level {
			case "error":
				errs++
			case "warn":
				warns++
			}
		}
		// Live = an open socket AND a recent heartbeat (catches half-open
		// sockets where conns is stuck but the device is actually gone).
		live := len(d.sinks) > 0 && time.Since(d.lastSeen) < 12*time.Second
		out = append(out, deviceInfo{
			ID: d.id, Label: d.label, UA: d.ua, GPU: d.gpu, IP: d.ip,
			W: d.w, H: d.h, DPR: d.dpr, URL: d.url,
			FirstSeen: d.firstSeen.UnixMilli(), LastSeen: d.lastSeen.UnixMilli(),
			Live: live, Count: len(d.lines), MaxSeq: d.nextSeq, Errors: errs, Warns: warns,
		})
		d.mu.Unlock()
	}
	// Stable tab order: oldest connection first.
	sort.Slice(out, func(i, j int) bool { return out[i].FirstSeen < out[j].FirstSeen })
	return out
}

func (d *device) linesSince(since int64, limit int) ([]logLine, int64) {
	d.mu.Lock()
	defer d.mu.Unlock()
	out := make([]logLine, 0)
	for _, ln := range d.lines {
		if ln.Seq > since {
			out = append(out, ln)
			if limit > 0 && len(out) >= limit {
				break
			}
		}
	}
	var oldest int64
	if len(d.lines) > 0 {
		oldest = d.lines[0].Seq
	}
	return out, oldest
}

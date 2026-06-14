package main

import (
	"encoding/json"
	"os"
	"sync"
)

// status is the JSON the editor polls. Field names must match the C# HostStatus
// class EXACTLY (Unity JsonUtility is name-sensitive). Written atomically via a
// temp file + rename so the editor never reads a half-written file.
type status struct {
	Pid            int    `json:"pid"`
	CloudflaredPid int    `json:"cloudflaredPid"`
	HTTPPort       int    `json:"httpPort"`
	HTTPSPort      int    `json:"httpsPort"`
	ControlPort    int    `json:"controlPort"`
	LocalURL       string `json:"localUrl"`
	LocalHTTPSURL  string `json:"localHttpsUrl"`
	LanURL         string `json:"lanUrl"`
	LanHTTPSURL    string `json:"lanHttpsUrl"`
	TunnelURL      string `json:"tunnelUrl"`
	TunnelOK       bool   `json:"tunnelOk"` // false once the tunnel is confirmed dead (process gone or probe failed)
}

type statusState struct {
	mu   sync.Mutex
	path string
	data status
}

// stat is the process-wide status, updated as ports bind and the tunnel
// resolves, and flushed to disk on every change.
var stat statusState

func (s *statusState) init(path string, d status) {
	s.mu.Lock()
	s.path = path
	s.data = d
	s.mu.Unlock()
	s.write()
}

func (s *statusState) setTunnel(url string, pid int) {
	s.mu.Lock()
	s.data.TunnelURL = url
	s.data.TunnelOK = url != ""
	if pid != 0 {
		s.data.CloudflaredPid = pid
	}
	s.mu.Unlock()
	s.write()
}

// setTunnelDown marks the tunnel unhealthy (cloudflared exited, or the health
// probe failed) while keeping the last URL, so the editor shows it as offline
// instead of a stale "active".
func (s *statusState) setTunnelDown() {
	s.mu.Lock()
	if !s.data.TunnelOK && s.data.TunnelURL == "" {
		s.mu.Unlock()
		return // never came up; nothing to mark
	}
	s.data.TunnelOK = false
	s.mu.Unlock()
	s.write()
}

func (s *statusState) setCloudflaredPid(pid int) {
	s.mu.Lock()
	s.data.CloudflaredPid = pid
	s.mu.Unlock()
	s.write()
}

func (s *statusState) cloudflaredPid() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.data.CloudflaredPid
}

func (s *statusState) write() {
	s.mu.Lock()
	path := s.path
	b, _ := json.MarshalIndent(s.data, "", "  ")
	s.mu.Unlock()
	if path == "" {
		return
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, b, 0o644); err == nil {
		_ = os.Rename(tmp, path) // atomic within the same directory
	}
}

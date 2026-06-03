package main

import (
	"bufio"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"os/exec"
	"regexp"
	"strings"
	"time"
)

var tunnelURLRe = regexp.MustCompile(`https://[a-z0-9-]+\.trycloudflare\.com`)

// findCloudflared resolves the cloudflared binary: an explicit path, then PATH,
// then the usual per-OS install locations. Returns "" if none runs.
func findCloudflared(explicit string) string {
	cands := []string{
		explicit,
		"cloudflared",
		`C:\Program Files (x86)\cloudflared\cloudflared.exe`,
		`C:\Program Files\cloudflared\cloudflared.exe`,
		"/opt/homebrew/bin/cloudflared",
		"/usr/local/bin/cloudflared",
		"/usr/bin/cloudflared",
	}
	for _, c := range cands {
		if c == "" {
			continue
		}
		if c != "cloudflared" {
			if _, err := os.Stat(c); err != nil {
				continue
			}
		}
		if exec.Command(c, "--version").Run() == nil {
			return c
		}
	}
	return ""
}

// startTunnel launches a Cloudflare quick tunnel against the local HTTP origin
// and publishes the trycloudflare URL to the status file when it resolves. The
// cloudflared PID is recorded immediately so the editor can kill it on Stop.
func startTunnel(exe string, httpPort int) {
	log.Printf("[tunnel] starting Cloudflare tunnel -> http://localhost:%d", httpPort)
	cmd := exec.Command(exe, "tunnel", "--url", fmt.Sprintf("http://localhost:%d", httpPort))
	pr, pw := io.Pipe()
	cmd.Stdout = pw
	cmd.Stderr = pw
	if err := cmd.Start(); err != nil {
		log.Printf("[tunnel] failed to start cloudflared: %v", err)
		return
	}
	stat.setCloudflaredPid(cmd.Process.Pid)

	// Closed when cloudflared exits, so the health monitor stops.
	done := make(chan struct{})
	go func() {
		_ = cmd.Wait()
		_ = pw.Close()
		stat.setTunnelDown() // process gone → the tunnel is dead
		close(done)
		log.Printf("[tunnel] cloudflared exited - tunnel marked offline")
	}()

	go func() {
		sc := bufio.NewScanner(pr)
		sc.Buffer(make([]byte, 64*1024), 1024*1024)
		found := false
		for sc.Scan() {
			line := sc.Text()
			if !found {
				if m := tunnelURLRe.FindString(line); m != "" {
					found = true
					log.Printf("[tunnel] public URL: %s", m)
					stat.setTunnel(m, cmd.Process.Pid)
					// Watch for a silently-dead tunnel (cloudflared can keep
					// running after the machine sleeps while the connection is gone).
					go monitorTunnel(m, done)
					continue
				}
			}
			// Quick tunnels print a benign "Cannot determine default origin
			// certificate path" ERR (that's for *named* tunnels) - the quick
			// tunnel resolves fine regardless, so don't alarm with it.
			if strings.Contains(line, "Cannot determine default origin certificate") {
				continue
			}
			low := strings.ToLower(line)
			if strings.Contains(low, " err ") || strings.Contains(low, "error") ||
				strings.Contains(low, "warn") || strings.Contains(low, "failed") {
				log.Printf("[tunnel] %s", line)
			}
		}
	}()
}

// monitorTunnel periodically checks that the public URL still reaches the local
// origin. cloudflared frequently KEEPS RUNNING after the machine sleeps while its
// quick-tunnel connection is permanently dead, so a live PID alone isn't enough -
// we probe end-to-end and mark the tunnel offline after two consecutive failures
// (the editor then shows it down instead of a stale "active"). Quick tunnels don't
// recover the same URL, so we stop once it's confirmed dead; Stop & Start reconnects.
func monitorTunnel(url string, done <-chan struct{}) {
	t := time.NewTicker(15 * time.Second)
	defer t.Stop()
	fails := 0
	for {
		select {
		case <-done:
			return // cloudflared exited; the Wait goroutine already marked it down
		case <-t.C:
			if tunnelAlive(url) {
				fails = 0
				continue
			}
			fails++
			log.Printf("[tunnel] health probe failed (%d/2): %s", fails, url)
			if fails >= 2 {
				log.Printf("[tunnel] tunnel unreachable (machine slept / network changed?) - marking offline. Stop & Start to reconnect.")
				stat.setTunnelDown()
				return
			}
		}
	}
}

// tunnelAlive reports whether an HTTP request to the public URL gets ANY response
// (even an error status) - proof it reached the local origin through the tunnel.
// A transport error / timeout means the tunnel is down.
func tunnelAlive(url string) bool {
	client := &http.Client{
		Timeout:       10 * time.Second,
		CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
	}
	resp, err := client.Get(url + "/__webhost/healthz")
	if err != nil {
		return false
	}
	_ = resp.Body.Close()
	return true
}

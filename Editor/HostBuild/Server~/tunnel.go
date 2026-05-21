package main

import (
	"bufio"
	"fmt"
	"io"
	"log"
	"os"
	"os/exec"
	"regexp"
	"strings"
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

	go func() {
		_ = cmd.Wait()
		_ = pw.Close()
		log.Printf("[tunnel] cloudflared exited")
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

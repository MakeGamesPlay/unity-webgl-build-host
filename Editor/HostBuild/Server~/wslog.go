package main

import (
	"bufio"
	"crypto/sha1"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"errors"
	"io"
	"log"
	"net"
	"net/http"
	"strings"
	"sync"
	"time"
)

// This file is a minimal RFC 6455 WebSocket server plus the per-device log hub.
// We hand-roll the protocol (rather than take a dependency) because our own
// injected shim is the only client, so we only need: accept the handshake,
// read masked text frames in, and write text/pong/close frames out.

const wsMaxFrame = 1 << 20 // 1 MiB - log lines never approach this

// logHub is the process-wide registry of connected devices and their buffers.
var logHub = newHub(5000)

// ─── Device + hub model ────────────────────────────────────────────

type logLine struct {
	Seq   int64  `json:"seq"`
	Level string `json:"level"`
	Msg   string `json:"msg"`
	Ts    int64  `json:"ts"`
	Frame int64  `json:"frame"`
}

type device struct {
	mu        sync.Mutex
	id        string
	label     string // UA-derived, e.g. "iPhone · Safari"
	ua        string
	gpu       string
	ip        string
	w, h      int
	dpr       float64
	url       string
	firstSeen time.Time
	lastSeen  time.Time
	sinks     []*wsConn // active WS connections (empty = stale)
	lines     []logLine
	nextSeq   int64
	maxLines  int
}

type hub struct {
	mu       sync.Mutex
	devices  map[string]*device
	maxLines int
}

func newHub(maxLines int) *hub {
	return &hub{devices: map[string]*device{}, maxLines: maxLines}
}

func (h *hub) get(id string) *device {
	h.mu.Lock()
	defer h.mu.Unlock()
	d := h.devices[id]
	if d == nil {
		now := time.Now()
		d = &device{id: id, firstSeen: now, lastSeen: now, maxLines: h.maxLines}
		h.devices[id] = d
	}
	return d
}

func (d *device) touch() {
	d.mu.Lock()
	d.lastSeen = time.Now()
	d.mu.Unlock()
}

func (d *device) addLine(level, msg string, ts, frame int64) {
	if ts == 0 {
		ts = time.Now().UnixMilli()
	}
	d.mu.Lock()
	d.nextSeq++
	d.lines = append(d.lines, logLine{Seq: d.nextSeq, Level: level, Msg: msg, Ts: ts, Frame: frame})
	if d.maxLines > 0 && len(d.lines) > d.maxLines {
		d.lines = d.lines[len(d.lines)-d.maxLines:] // ring buffer
	}
	d.lastSeen = time.Now()
	d.mu.Unlock()
}

func (d *device) labelOrID() string {
	if d.label != "" {
		return d.label
	}
	return d.id
}

// ─── Incoming message shape (from the shim) ────────────────────────

type inbound struct {
	T     string  `json:"t"` // "hello" | "log" | "ping"
	Level string  `json:"level"`
	Msg   string  `json:"msg"`
	Ts    int64   `json:"ts"`
	Frame int64   `json:"frame"`
	UA    string  `json:"ua"`
	GPU   string  `json:"gpu"`
	W     int     `json:"w"`
	H     int     `json:"h"`
	DPR   float64 `json:"dpr"`
	URL   string  `json:"url"`
}

func (h *hub) handleMessage(d *device, data []byte) {
	var m inbound
	if json.Unmarshal(data, &m) != nil {
		return
	}
	switch m.T {
	case "hello":
		d.mu.Lock()
		d.ua, d.gpu, d.url = m.UA, m.GPU, m.URL
		d.w, d.h, d.dpr = m.W, m.H, m.DPR
		d.label = uaLabel(m.UA)
		d.lastSeen = time.Now()
		lbl := d.labelOrID()
		d.mu.Unlock()
		log.Printf("[dev %s] hello ua=%q gpu=%q %dx%d@%g", lbl, m.UA, m.GPU, m.W, m.H, m.DPR)
	case "log":
		d.addLine(m.Level, m.Msg, m.Ts, m.Frame)
		log.Printf("[dev %s] %s %s", d.labelOrID(), strings.ToUpper(m.Level), m.Msg)
	case "ping":
		d.touch()
	}
}

// ─── WebSocket serving ─────────────────────────────────────────────

// wsConn wraps one connection's frame writer with a mutex so the read-loop's
// pong/close writes and any server->device pushes never interleave on the wire.
type wsConn struct {
	w  *bufio.Writer
	mu sync.Mutex
}

func (c *wsConn) writeFrame(op byte, payload []byte) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	return writeFrame(c.w, op, payload)
}

func (c *wsConn) sendText(s string) { _ = c.writeFrame(opText, []byte(s)) }

// sendAll pushes a text message to every live connection of the device.
func (d *device) sendAll(text string) {
	d.mu.Lock()
	sinks := append([]*wsConn(nil), d.sinks...)
	d.mu.Unlock()
	for _, s := range sinks {
		s.sendText(text)
	}
}

// broadcast pushes a message to one device (deviceID set) or all (empty).
func (h *hub) broadcast(text, deviceID string) {
	h.mu.Lock()
	var devs []*device
	for _, d := range h.devices {
		if deviceID == "" || d.id == deviceID {
			devs = append(devs, d)
		}
	}
	h.mu.Unlock()
	for _, d := range devs {
		d.sendAll(text)
	}
}

func (h *hub) serveWS(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("id")
	if id == "" {
		id = "anon-" + r.RemoteAddr
	}
	d := h.get(id)

	conn, brw, err := upgradeWS(w, r)
	if err != nil {
		http.Error(w, "websocket upgrade failed", http.StatusBadRequest)
		return
	}
	defer conn.Close()

	ip := r.RemoteAddr
	if host, _, e := net.SplitHostPort(ip); e == nil {
		ip = host
	}

	c := &wsConn{w: brw.Writer}
	d.mu.Lock()
	d.sinks = append(d.sinks, c)
	d.ip = ip
	d.lastSeen = time.Now()
	lbl := d.labelOrID()
	d.mu.Unlock()
	log.Printf("[dev %s] connected (%s)", lbl, ip)

	defer func() {
		d.mu.Lock()
		for i, s := range d.sinks {
			if s == c {
				d.sinks = append(d.sinks[:i], d.sinks[i+1:]...)
				break
			}
		}
		d.lastSeen = time.Now()
		lbl := d.labelOrID()
		d.mu.Unlock()
		log.Printf("[dev %s] disconnected", lbl)
	}()

	var frag []byte
	var fragOp byte
	for {
		op, payload, fin, err := readFrame(brw.Reader)
		if err != nil {
			return
		}
		switch op {
		case opPing:
			_ = c.writeFrame(opPong, payload)
		case opPong:
			d.touch()
		case opClose:
			_ = c.writeFrame(opClose, nil)
			return
		case opText, opBinary:
			frag = append(frag[:0], payload...)
			fragOp = op
			if fin {
				if fragOp == opText {
					h.handleMessage(d, frag)
				}
				frag = frag[:0]
			}
		case opContinuation:
			frag = append(frag, payload...)
			if fin {
				if fragOp == opText {
					h.handleMessage(d, frag)
				}
				frag = frag[:0]
			}
		}
	}
}

// ─── Frame protocol (RFC 6455 subset) ──────────────────────────────

const (
	opContinuation = 0x0
	opText         = 0x1
	opBinary       = 0x2
	opClose        = 0x8
	opPing         = 0x9
	opPong         = 0xA
)

const wsGUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

func wsAccept(key string) string {
	h := sha1.New()
	io.WriteString(h, key+wsGUID)
	return base64.StdEncoding.EncodeToString(h.Sum(nil))
}

// upgradeWS performs the HTTP->WebSocket handshake and hijacks the connection.
// Works transparently over TLS (the hijacked conn is the *tls.Conn).
func upgradeWS(w http.ResponseWriter, r *http.Request) (net.Conn, *bufio.ReadWriter, error) {
	if !strings.Contains(strings.ToLower(r.Header.Get("Connection")), "upgrade") ||
		!strings.EqualFold(r.Header.Get("Upgrade"), "websocket") {
		return nil, nil, errors.New("not a websocket upgrade")
	}
	key := r.Header.Get("Sec-WebSocket-Key")
	if key == "" {
		return nil, nil, errors.New("missing Sec-WebSocket-Key")
	}
	hj, ok := w.(http.Hijacker)
	if !ok {
		return nil, nil, errors.New("response writer does not support hijack")
	}
	conn, brw, err := hj.Hijack()
	if err != nil {
		return nil, nil, err
	}
	resp := "HTTP/1.1 101 Switching Protocols\r\n" +
		"Upgrade: websocket\r\n" +
		"Connection: Upgrade\r\n" +
		"Sec-WebSocket-Accept: " + wsAccept(key) + "\r\n\r\n"
	if _, err := brw.WriteString(resp); err != nil {
		conn.Close()
		return nil, nil, err
	}
	if err := brw.Flush(); err != nil {
		conn.Close()
		return nil, nil, err
	}
	return conn, brw, nil
}

// readFrame reads one frame. Client->server frames are always masked; we unmask
// in place. Returns the opcode, the (unmasked) payload, and the FIN bit.
func readFrame(r *bufio.Reader) (opcode byte, payload []byte, fin bool, err error) {
	var hdr [2]byte
	if _, err = io.ReadFull(r, hdr[:]); err != nil {
		return
	}
	fin = hdr[0]&0x80 != 0
	opcode = hdr[0] & 0x0F
	masked := hdr[1]&0x80 != 0
	ln := int64(hdr[1] & 0x7F)
	switch ln {
	case 126:
		var ext [2]byte
		if _, err = io.ReadFull(r, ext[:]); err != nil {
			return
		}
		ln = int64(binary.BigEndian.Uint16(ext[:]))
	case 127:
		var ext [8]byte
		if _, err = io.ReadFull(r, ext[:]); err != nil {
			return
		}
		ln = int64(binary.BigEndian.Uint64(ext[:]))
	}
	if ln < 0 || ln > wsMaxFrame {
		err = errors.New("websocket frame too large")
		return
	}
	var mask [4]byte
	if masked {
		if _, err = io.ReadFull(r, mask[:]); err != nil {
			return
		}
	}
	payload = make([]byte, ln)
	if _, err = io.ReadFull(r, payload); err != nil {
		return
	}
	if masked {
		for i := range payload {
			payload[i] ^= mask[i&3]
		}
	}
	return
}

// writeFrame writes a single unmasked server->client frame (FIN set).
func writeFrame(w *bufio.Writer, opcode byte, payload []byte) error {
	hdr := []byte{0x80 | opcode}
	ln := len(payload)
	switch {
	case ln < 126:
		hdr = append(hdr, byte(ln))
	case ln < 65536:
		var ext [2]byte
		binary.BigEndian.PutUint16(ext[:], uint16(ln))
		hdr = append(hdr, 126)
		hdr = append(hdr, ext[:]...)
	default:
		var ext [8]byte
		binary.BigEndian.PutUint64(ext[:], uint64(ln))
		hdr = append(hdr, 127)
		hdr = append(hdr, ext[:]...)
	}
	if _, err := w.Write(hdr); err != nil {
		return err
	}
	if _, err := w.Write(payload); err != nil {
		return err
	}
	return w.Flush()
}

// ─── User-Agent → friendly label ───────────────────────────────────

func uaLabel(ua string) string {
	osName := "Device"
	switch {
	case strings.Contains(ua, "iPhone"):
		osName = "iPhone"
	case strings.Contains(ua, "iPad"):
		osName = "iPad"
	case strings.Contains(ua, "Android"):
		osName = androidModel(ua)
	case strings.Contains(ua, "Windows"):
		osName = "Windows"
	case strings.Contains(ua, "Macintosh"), strings.Contains(ua, "Mac OS X"):
		osName = "Mac"
	case strings.Contains(ua, "CrOS"):
		osName = "ChromeOS"
	case strings.Contains(ua, "Linux"):
		osName = "Linux"
	}
	browser := "Browser"
	switch {
	case strings.Contains(ua, "Edg/"):
		browser = "Edge"
	case strings.Contains(ua, "OPR/"), strings.Contains(ua, "Opera"):
		browser = "Opera"
	case strings.Contains(ua, "Firefox/"), strings.Contains(ua, "FxiOS/"):
		browser = "Firefox"
	case strings.Contains(ua, "CriOS/"), strings.Contains(ua, "Chrome/"):
		browser = "Chrome"
	case strings.Contains(ua, "Safari/"):
		browser = "Safari"
	}
	return osName + " · " + browser
}

func androidModel(ua string) string {
	i := strings.Index(ua, "Android")
	if i < 0 {
		return "Android"
	}
	rest := ua[i:]
	if semi := strings.Index(rest, "; "); semi >= 0 {
		rest = rest[semi+2:]
	} else {
		return "Android"
	}
	end := len(rest)
	if b := strings.Index(rest, " Build/"); b >= 0 && b < end {
		end = b
	}
	if p := strings.Index(rest, ")"); p >= 0 && p < end {
		end = p
	}
	model := strings.TrimSpace(rest[:end])
	if model == "" || len(model) > 30 {
		return "Android"
	}
	return model
}

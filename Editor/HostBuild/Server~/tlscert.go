package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"math/big"
	"net"
	"os"
	"time"
)

// generateSelfSigned mints an in-memory self-signed certificate covering the
// given hosts (DNS names + IPs go into the Subject Alternative Name). It's for
// LAN HTTPS so a phone gets a *secure context* (camera / WebXR / SharedArray-
// Buffer) — the user taps through the "not trusted" warning once per device,
// after which the origin is treated as secure.
//
// Design choices for broad device acceptance:
//   - ECDSA P-256: supported by every current mobile browser, small + fast.
//   - SAN-only identity (no CN reliance): modern browsers ignore CN.
//   - ExtKeyUsage serverAuth + ~395-day validity: stays within the limits
//     Apple/Chrome enforce, so the same cert also works if later trusted via
//     an installed CA rather than a manual override.
func generateSelfSigned(hosts []string) (tls.Certificate, error) {
	priv, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return tls.Certificate{}, err
	}

	serial, err := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 128))
	if err != nil {
		return tls.Certificate{}, err
	}

	tmpl := x509.Certificate{
		SerialNumber: serial,
		Subject: pkix.Name{
			CommonName:   "WebGL Build Host (dev)",
			Organization: []string{"WebGL Build Host"},
		},
		NotBefore:             time.Now().Add(-1 * time.Hour),
		NotAfter:              time.Now().Add(395 * 24 * time.Hour),
		KeyUsage:              x509.KeyUsageDigitalSignature,
		ExtKeyUsage:           []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		BasicConstraintsValid: true,
	}
	for _, h := range hosts {
		if ip := net.ParseIP(h); ip != nil {
			tmpl.IPAddresses = append(tmpl.IPAddresses, ip)
		} else {
			tmpl.DNSNames = append(tmpl.DNSNames, h)
		}
	}

	der, err := x509.CreateCertificate(rand.Reader, &tmpl, &tmpl, &priv.PublicKey, priv)
	if err != nil {
		return tls.Certificate{}, err
	}
	keyDER, err := x509.MarshalECPrivateKey(priv)
	if err != nil {
		return tls.Certificate{}, err
	}
	certPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})
	keyPEM := pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: keyDER})
	return tls.X509KeyPair(certPEM, keyPEM)
}

// serverHosts builds the identity list the cert should cover: localhost, the
// loopback IPs, the machine hostname + its mDNS ".local" form (so phones can
// resolve it via Bonjour even as the DHCP IP changes), and every private IPv4
// the machine currently has.
func serverHosts() []string {
	hosts := []string{"localhost", "127.0.0.1", "::1"}
	if hn, err := os.Hostname(); err == nil && hn != "" {
		hosts = append(hosts, hn, hn+".local")
	}
	hosts = append(hosts, lanIPv4s()...)
	return dedup(hosts)
}

// lanIPv4s returns the machine's up, non-loopback, private IPv4 addresses -
// the addresses a phone on the same Wi-Fi can actually reach.
func lanIPv4s() []string {
	var out []string
	ifaces, err := net.Interfaces()
	if err != nil {
		return out
	}
	for _, ifc := range ifaces {
		if ifc.Flags&net.FlagUp == 0 || ifc.Flags&net.FlagLoopback != 0 {
			continue
		}
		addrs, _ := ifc.Addrs()
		for _, a := range addrs {
			var ip net.IP
			switch v := a.(type) {
			case *net.IPNet:
				ip = v.IP
			case *net.IPAddr:
				ip = v.IP
			}
			ip4 := ip.To4()
			if ip4 == nil || ip4.IsLoopback() {
				continue
			}
			if ip4.IsPrivate() {
				out = append(out, ip4.String())
			}
		}
	}
	return out
}

// primaryLANIP returns the IP of the interface that routes to the internet -
// i.e. the real Wi-Fi/Ethernet adapter a phone shares the network with, NOT a
// VirtualBox/WSL/Hyper-V virtual adapter. The UDP "dial" sends no packets; it
// just makes the OS pick the outbound route. Falls back to the first private
// IPv4 if there's no default route.
func primaryLANIP() string {
	if conn, err := net.Dial("udp", "8.8.8.8:80"); err == nil {
		defer conn.Close()
		if la, ok := conn.LocalAddr().(*net.UDPAddr); ok && la.IP.To4() != nil {
			return la.IP.String()
		}
	}
	if ips := lanIPv4s(); len(ips) > 0 {
		return ips[0]
	}
	return ""
}

func dedup(in []string) []string {
	seen := map[string]bool{}
	var out []string
	for _, s := range in {
		if s == "" || seen[s] {
			continue
		}
		seen[s] = true
		out = append(out, s)
	}
	return out
}

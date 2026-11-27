package bot

import (
	"bufio"
	"crypto/tls"
	"encoding/base64"
	"encoding/pem"
	"io"
	"net"
	"net/http/httptest"
	"net/mail"
	"net/textproto"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
)

// Exercise the production TLS validation and SMTP client against a disposable,
// authenticated MailServer fixture. The child trusts only the fixture's test CA
// in addition to its operating system's certificates.
func TestToolboxSMTPSession(t *testing.T) {
	if port := os.Getenv("TOOLBOX_TEST_SMTP_PORT"); port != "" {
		var cfg tbConfig
		cfg.SMTP.Host = "127.0.0.1"
		cfg.SMTP.Port, _ = strconv.Atoi(port)
		cfg.SMTP.From = "relay@example.invalid"
		cfg.SMTP.Username = cfg.SMTP.From
		for _, route := range []string{"email", "dm"} {
			m := tbEnvelope{route, "owner@example.invalid", strings.Repeat("a", 20), strings.Repeat("b", 48), "Discord 1\n\n留言"}
			if err := tbSMTP(t.Context(), cfg, m); err != nil {
				t.Fatal(err)
			}
		}
		return
	}
	template := httptest.NewTLSServer(nil)
	cert := template.TLS.Certificates[0]
	template.Close()
	ca := filepath.Join(t.TempDir(), "fixture.pem")
	if err := os.WriteFile(ca, pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: cert.Certificate[0]}), 0600); err != nil {
		t.Fatal(err)
	}
	listener, err := tls.Listen("tcp", "127.0.0.1:0", &tls.Config{Certificates: []tls.Certificate{cert}, MinVersion: tls.VersionTLS12})
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	port := listener.Addr().(*net.TCPAddr).Port
	cmd := exec.CommandContext(t.Context(), os.Args[0], "-test.run=^TestToolboxSMTPSession$")
	cmd.Env = append(os.Environ(), "TOOLBOX_TEST_SMTP_PORT="+strconv.Itoa(port), "SSL_CERT_FILE="+ca, "LIULIAN_TOOLBOX_SMTP_PASSWORD=fixture")
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	defer func() { _ = cmd.Process.Kill() }()
	for _, route := range []string{"email", "dm"} {
		conn, err := listener.Accept()
		if err != nil {
			t.Fatal(err)
		}
		peer := textproto.NewConn(conn)
		expect := func(prefix string) string {
			t.Helper()
			v, err := peer.ReadLine()
			if err != nil || !strings.HasPrefix(v, prefix) {
				t.Fatalf("unexpected SMTP command for %s", prefix)
			}
			return v
		}
		answer := func(line string) {
			t.Helper()
			if err := peer.PrintfLine("%s", line); err != nil {
				t.Fatal(err)
			}
		}
		answer("220 fixture")
		expect("EHLO ")
		answer("250-fixture")
		answer("250-DSN")
		answer("250 AUTH PLAIN")
		expect("AUTH PLAIN ")
		answer("235 authenticated")
		from := expect("MAIL FROM:<relay@example.invalid>")
		answer("250 accepted")
		to := expect("RCPT TO:<owner@example.invalid>")
		answer("250 accepted")
		if (route == "email") != strings.Contains(from, "ENVID=") || (route == "email") != strings.Contains(to, "NOTIFY=SUCCESS,FAILURE,DELAY") {
			t.Fatal("DSN negotiation", from, to)
		}
		expect("DATA")
		answer("354 enter message")
		msg, err := mail.ReadMessage(bufio.NewReader(peer.DotReader()))
		if err != nil {
			t.Fatal(err)
		}
		body, err := io.ReadAll(base64.NewDecoder(base64.StdEncoding, msg.Body))
		if err != nil || string(body) != "Discord 1\n\n留言" {
			t.Fatal("changed SMTP payload", err)
		}
		if (route == "dm") != (msg.Header.Get("X-ToolBox-Proof") != "") {
			t.Fatal("wrong relay proof")
		}
		answer("250 queued")
		peer.Close()
	}
	if err := cmd.Wait(); err != nil {
		t.Fatal(err)
	}
}

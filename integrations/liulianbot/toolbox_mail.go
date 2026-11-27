package bot

import (
	"context"
	"crypto/tls"
	"encoding/base64"
	"fmt"
	"net"
	"net/mail"
	"net/smtp"
	"os"
	"strconv"
	"strings"
	"time"

	dg "github.com/bwmarrin/discordgo"
)

func tbAddress(addr string) bool {
	a, err := mail.ParseAddress(addr)
	return err == nil && a.Address == addr && !strings.ContainsAny(addr, "\r\n")
}

type tbEnvelope struct {
	Route, To, ID, Proof, Body string
}

// net/smtp exposes its text protocol connection for extensions such as RFC 3461.
func tbSMTPCommand(client *smtp.Client, command string, args ...any) error {
	id, err := client.Text.Cmd(command, args...)
	if err != nil {
		return err
	}
	client.Text.StartResponse(id)
	defer client.Text.EndResponse(id)
	_, _, err = client.Text.ReadResponse(250)
	return err
}

func tbSMTP(ctx context.Context, c tbConfig, e tbEnvelope) error {
	m := c.SMTP
	password := os.Getenv("LIULIAN_TOOLBOX_SMTP_PASSWORD")
	if !tbAddress(m.From) || !tbAddress(e.To) || m.Host == "" || m.Username == "" || password == "" {
		return tbError("MailUnavailable")
	}
	if m.Port == 0 {
		m.Port = 465
	}
	dial := tls.Dialer{NetDialer: &net.Dialer{Timeout: 5 * time.Second}, Config: &tls.Config{ServerName: m.Host, MinVersion: tls.VersionTLS12}}
	conn, err := dial.DialContext(ctx, "tcp", net.JoinHostPort(m.Host, strconv.Itoa(m.Port)))
	if err != nil {
		return tbError("MailUnavailable")
	}
	defer conn.Close()
	_ = conn.SetDeadline(time.Now().Add(12 * time.Second))
	client, err := smtp.NewClient(conn, m.Host)
	if err != nil {
		return tbError("MailUnavailable")
	}
	defer client.Close()
	if client.Auth(smtp.PlainAuth("", m.Username, password, m.Host)) != nil {
		return tbError("MailUnavailable")
	}
	if dsn, _ := client.Extension("DSN"); dsn && e.Route == "email" {
		err = tbSMTPCommand(client, "MAIL FROM:<%s> RET=HDRS ENVID=%s.%s", m.From, e.ID, e.Proof)
		if err == nil {
			err = tbSMTPCommand(client, "RCPT TO:<%s> NOTIFY=SUCCESS,FAILURE,DELAY", e.To)
		}
	} else {
		err = client.Mail(m.From)
		if err == nil {
			err = client.Rcpt(e.To)
		}
	}
	if err != nil {
		return tbError("MailUnavailable")
	}
	w, err := client.Data()
	if err != nil {
		return tbError("MailUnavailable")
	}
	header := "From: " + m.From + "\r\nTo: " + e.To + "\r\nSubject: LiuLianToolBox\r\nDate: " + time.Now().Format(time.RFC1123Z) + "\r\n"
	if e.Route == "dm" {
		header += "X-ToolBox-ID: " + e.ID + "\r\nX-ToolBox-Proof: " + e.Proof + "\r\n"
	}
	header += "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n"
	encoded := base64.StdEncoding.EncodeToString([]byte(e.Body))
	var msg strings.Builder
	msg.WriteString(header)
	for len(encoded) > 0 {
		n := min(76, len(encoded))
		msg.WriteString(encoded[:n] + "\r\n")
		encoded = encoded[n:]
	}
	if _, err := fmt.Fprint(w, msg.String()); err != nil {
		return tbError("MailUnavailable")
	}
	if err := w.Close(); err != nil {
		return tbError("Uncertain")
	}
	return nil // SMTP accepted the queued message.
}

func (s *tbBridge) submitMail(ctx context.Context, lang dg.Locale, sender, owner, text string) (string, error) {
	if !tbText(text, 1000) {
		return "", tbError("InvalidInput")
	}
	cfg, err := tbSettings(s.configPath)
	if err != nil {
		return "", err
	}
	s.mu.Lock()
	grant := cfg.Users[owner]
	p := s.data.Users[owner]
	if p == nil || !grant.Idle || !grant.Mailbox || !p.Mailbox.Enabled || !s.isIdle(owner, p.Idle) {
		s.mu.Unlock()
		return "", tbError("Denied")
	}
	route := p.Mailbox.Route
	to := grant.Email
	proof := tbRandom()
	if route == "dm" {
		to = cfg.SMTP.RelayInbox
	}
	if !tbAddress(to) {
		s.mu.Unlock()
		return "", tbError("MailUnavailable")
	}
	id := tbRandom()[:20]
	body := "Discord " + sender + "\n\n" + text
	err = s.change(func() {
		for key, r := range s.data.Receipts {
			if r.At.Before(s.now().AddDate(0, 0, -30)) {
				delete(s.data.Receipts, key)
			}
		}
		s.data.Receipts[id] = &tbReceipt{owner, sender, route, "Submitting", tbHash(proof), tbHash(body), s.now()}
	})
	s.mu.Unlock()
	if err != nil {
		return "", err
	}
	result := "MailAccepted"
	if err = s.sendMail(ctx, cfg, tbEnvelope{route, to, id, proof, body}); err != nil {
		result = "MailUnavailable"
		if err == tbError("Uncertain") {
			result = "Uncertain"
		}
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	rec := s.data.Receipts[id]
	if rec.Status == "Submitting" {
		if e := s.change(func() { rec.Status = result }); e != nil {
			return "", e
		}
	} else {
		result = rec.Status
	}
	return tbT(lang, "ReceiptIssued", tbT(lang, result), id), nil
}

func (s *tbBridge) mailReceived(ctx context.Context, in tbRequest) error {
	// The relay can only complete the specific envelope it received via SMTP.
	s.mailMu.Lock()
	defer s.mailMu.Unlock()
	s.mu.Lock()
	r := s.data.Receipts[in.ID]
	if r == nil || r.Route != "dm" || in.Proof == "" || r.ProofHash != tbHash(in.Proof) || r.BodyHash != tbHash(in.Body) {
		s.mu.Unlock()
		return tbError("Denied")
	}
	if r.Status == "DmDelivered" {
		s.mu.Unlock()
		return nil
	}
	if r.Status == "DeliveryReview" {
		s.mu.Unlock()
		return tbError("Uncertain")
	}
	c, err := tbSettings(s.configPath)
	if err != nil {
		s.mu.Unlock()
		return err
	}
	p := s.data.Users[r.Owner]
	if !c.Users[r.Owner].Mailbox || p == nil || !p.Mailbox.Enabled || p.Mailbox.Route != "dm" {
		err := s.change(func() { r.Status = "Denied" })
		s.mu.Unlock()
		if err != nil {
			return err
		}
		return tbError("Denied")
	}
	if r.Status == "Denied" {
		s.mu.Unlock()
		return tbError("Denied")
	}
	owner := r.Owner
	// A crash between Discord accepting a DM and persisting its receipt needs review.
	err = s.change(func() { r.Status = "DeliveryReview" })
	s.mu.Unlock()
	if err != nil {
		return err
	}
	delivery, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	if err = s.deliverDM(delivery, owner, in.Body, in.ID); err != nil {
		return tbError("Uncertain")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.change(func() { s.data.Receipts[in.ID].Status = "DmDelivered" })
}

// Delivery reports arrive in the envelope sender's inbox. Bind them to the
// original SMTP transaction before accepting a terminal or delayed status.
func (s *tbBridge) mailStatus(in tbRequest) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	r := s.data.Receipts[in.ID]
	if r == nil || r.Route != "email" || in.Proof == "" || r.ProofHash != tbHash(in.Proof) {
		return tbError("Denied")
	}
	switch in.Result {
	case "EmailDelivered", "EmailFailed", "EmailDelayed":
	default:
		return tbError("InvalidInput")
	}
	if r.Status == "EmailDelivered" || r.Status == "EmailFailed" || r.Status == in.Result {
		return nil
	}
	return s.change(func() { r.Status = in.Result })
}

package bot

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
	"slices"
	"strings"
	"time"
)

type tbRequest struct {
	Code, Name, Target, App, ID, Result, Source   string
	IdleSeconds                                   int64
	Apps                                          []tbApp
	Acks                                          []string
	Enabled                                       bool
	Minutes, Limit                                int
	Reply, Route, GuildID, ChannelID, Proof, Body string
}

func (s *tbBridge) start(b *Bot) error {
	l, err := net.Listen("tcp", s.listen)
	if err != nil {
		return err
	}
	server := &http.Server{Handler: http.HandlerFunc(s.serve), ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout: 8 * time.Second, WriteTimeout: 24 * time.Second, IdleTimeout: 45 * time.Second, MaxHeaderBytes: 8192}
	b.work(func() {
		<-b.ctx.Done()
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = server.Shutdown(ctx)
	})
	b.work(func() {
		if err := server.Serve(l); err != nil && !errors.Is(err, http.ErrServerClosed) {
			report("ToolBox listener", err)
		}
	})
	return nil
}

func (s *tbBridge) serve(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	if r.Method != "POST" {
		w.Header().Set("Allow", "POST")
		tbWriteError(w, 405, "InvalidInput")
		return
	}
	if !strings.HasPrefix(r.URL.Path, "/v1/") {
		tbWriteError(w, 404, "InvalidInput")
		return
	}
	var in tbRequest
	decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 65536))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&in) != nil || decoder.Decode(&struct{}{}) != io.EOF {
		tbWriteError(w, 400, "InvalidInput")
		return
	}
	route := strings.TrimPrefix(r.URL.Path, "/v1/")
	out, err := s.dispatch(r, route, in)
	if err != nil {
		var e tbError
		if !errors.As(err, &e) {
			report("ToolBox "+route, err)
			tbWriteError(w, 503, "BotUnavailable")
			return
		}
		status := 400
		switch e {
		case "Unauthorized":
			status = 401
		case "Denied":
			status = 403
		case "Offline", "Uncertain", "AlreadyPaired", "TooMany":
			status = 409
		}
		tbWriteError(w, status, string(e))
		return
	}
	if out == nil {
		out = map[string]string{"code": "Saved"}
	}
	_ = json.NewEncoder(w).Encode(out)
}

func tbWriteError(w http.ResponseWriter, status int, code string) {
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(map[string]string{"code": code})
}

func (s *tbBridge) dispatch(r *http.Request, route string, in tbRequest) (any, error) {
	if route == "mail/status" {
		return nil, s.mailStatus(in)
	}
	if route == "mail/received" {
		return nil, s.mailReceived(r.Context(), in)
	}
	s.mu.Lock()
	if route == "link" {
		defer s.mu.Unlock()
		return s.enroll(in.Code, strings.TrimSpace(in.Name))
	}
	d := s.authenticate(r)
	if d == nil {
		s.mu.Unlock()
		return nil, tbError("Unauthorized")
	}
	switch route {
	case "launch":
		job, err := s.queue(d, in.Target, in.App)
		s.mu.Unlock()
		if err != nil {
			return nil, err
		}
		return s.wait(r.Context(), job)
	case "voice/join":
		id := d.ID
		s.mu.Unlock()
		ctx, cancel := context.WithTimeout(r.Context(), 8*time.Second)
		defer cancel()
		if err := s.move(ctx, id, in); err != nil {
			return nil, err
		}
		return map[string]string{"code": "MoveRequested"}, nil
	case "channels", "channel":
		owner := d.Owner
		s.mu.Unlock()
		ctx, cancel := context.WithTimeout(r.Context(), 8*time.Second)
		defer cancel()
		if route == "channels" {
			return s.channels(ctx, owner)
		}
		if !tbText(in.Name, 100) || in.Limit < 0 || in.Limit > 99 || snow(in.GuildID) == "" || snow(in.ChannelID) == "" {
			return nil, tbError("InvalidInput")
		}
		return nil, s.edit(ctx, owner, tbPrivate{in.GuildID, in.ChannelID, strings.TrimSpace(in.Name), in.Limit})
	}
	defer s.mu.Unlock()
	switch route {
	case "sync":
		if !tbText(in.Name, 64) || len(in.Apps) > 128 || in.IdleSeconds < 0 || len(in.Acks) > 128 {
			return nil, tbError("InvalidInput")
		}
		seen := map[string]bool{}
		for _, a := range in.Apps {
			if !tbText(a.ID, 64) || !tbText(a.Name, 100) || seen[a.ID] {
				return nil, tbError("InvalidInput")
			}
			seen[a.ID] = true
		}
		c, err := tbSettings(s.configPath)
		if err != nil {
			return nil, err
		}
		acked := func(a tbAlarm) bool { return slices.Contains(in.Acks, a.ID) }
		if d.Name != in.Name || slices.ContainsFunc(d.Alarms, acked) {
			if err := s.change(func() { d.Name = in.Name; d.Alarms = slices.DeleteFunc(d.Alarms, acked) }); err != nil {
				return nil, err
			}
		}
		s.live[d.ID] = tbLive{s.now(), in.IdleSeconds, in.Apps}
		return s.snapshot(d, c.Users[d.Owner]), nil
	case "unlink":
		return nil, s.revoke(d)
	case "pair/invite":
		if d.Peer != "" {
			return nil, tbError("AlreadyPaired")
		}
		return map[string]string{"code": s.issue(s.invites, d.ID)}, nil
	case "pair/accept":
		id, err := s.code(s.invites, in.Code)
		if err != nil {
			return nil, err
		}
		p := s.data.Devices[id]
		if p == nil || p.ID == d.ID {
			return nil, tbError("InvalidCode")
		}
		if d.Peer != "" || p.Peer != "" {
			return nil, tbError("AlreadyPaired")
		}
		err = s.change(func() { d.Peer = p.ID; p.Peer = d.ID })
		if err == nil {
			for k, v := range s.invites {
				if v.Subject == d.ID || v.Subject == p.ID {
					delete(s.invites, k)
				}
			}
		}
		return nil, err
	case "pair/remove":
		if in.Target == "" || in.Target != d.Peer {
			return nil, tbError("Denied")
		}
		err := s.change(func() { s.clearPair(d) })
		if err == nil {
			s.stopJobs(d.ID)
		}
		return nil, err
	case "pair/share":
		p := s.data.Devices[d.Peer]
		if p == nil || p.Peer != d.ID || in.ID != p.ID {
			return nil, tbError("Denied")
		}
		if in.Target != "" && in.Target != p.ID && in.Target != d.ID {
			return nil, tbError("InvalidInput")
		}
		return nil, s.change(func() { p.Presenter = in.Target; d.Presenter = in.Target })
	case "result":
		j := s.jobs[in.ID]
		if j == nil {
			return nil, nil
		}
		if j.Target != d.ID || j.From != d.Peer {
			return nil, tbError("Denied")
		}
		if !slices.Contains([]string{"Launched", "LaunchFailed", "AppMissing", "Denied", "Uncertain"}, in.Result) {
			return nil, tbError("InvalidInput")
		}
		select {
		case j.Done <- in.Result:
		default:
		}
		return nil, nil
	case "default":
		if in.Target != "" {
			target := s.data.Devices[in.Target]
			if target == nil || target.Owner != d.Owner {
				return nil, tbError("Denied")
			}
		}
		return nil, s.change(func() { s.data.user(d.Owner).DefaultDevice = in.Target })
	case "idle", "mailbox":
		c, err := tbSettings(s.configPath)
		if err != nil {
			return nil, err
		}
		g := c.Users[d.Owner]
		if route == "idle" {
			if !g.Idle {
				return nil, tbError("Denied")
			}
			if in.Minutes < 1 || in.Minutes > 1440 || len([]rune(in.Reply)) > 1000 || (in.Enabled && !tbText(in.Reply, 1000)) {
				return nil, tbError("InvalidInput")
			}
			return nil, s.change(func() { s.data.user(d.Owner).Idle = tbIdle{in.Enabled, in.Minutes, in.Reply} })
		}
		if !g.Mailbox {
			return nil, tbError("Denied")
		}
		if in.Route != "dm" && in.Route != "email" {
			return nil, tbError("InvalidInput")
		}
		if in.Enabled && in.Route == "email" && g.Email == "" {
			return nil, tbError("NoEmail")
		}
		return nil, s.change(func() { s.data.user(d.Owner).Mailbox = tbMailbox{in.Enabled, in.Route} })
	default:
		return nil, tbError("InvalidInput")
	}
}

func (s *tbBridge) queue(d *tbDevice, target, app string) (*tbWaiting, error) {
	p := s.data.Devices[target]
	if p == nil || d.Peer != target || p.Peer != d.ID {
		return nil, tbError("Denied")
	}
	if !s.online(p.ID) {
		return nil, tbError("Offline")
	}
	if !slices.ContainsFunc(s.live[p.ID].Apps, func(a tbApp) bool { return a.ID == app }) {
		return nil, tbError("AppMissing")
	}
	for _, j := range s.jobs {
		if j.From == d.ID {
			return nil, tbError("TooMany")
		}
	}
	j := &tbWaiting{tbLaunch: tbLaunch{tbRandom(), d.ID, target, app}, Done: make(chan string, 1)}
	s.jobs[j.ID] = j
	return j, nil
}

func (s *tbBridge) wait(ctx context.Context, j *tbWaiting) (any, error) {
	var result string
	select {
	case result = <-j.Done:
	case <-time.After(12 * time.Second):
	case <-ctx.Done():
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.jobs, j.ID)
	// A result can arrive at the same instant as the timeout.
	if result == "" {
		select {
		case result = <-j.Done:
		default:
		}
	}
	if result != "" {
		return map[string]string{"code": result}, nil
	}
	if j.Offered {
		return nil, tbError("Uncertain")
	}
	return nil, tbError("Offline")
}

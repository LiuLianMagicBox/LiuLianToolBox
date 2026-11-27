package bot

import (
	"bytes"
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/LiuLianMagicBox/LiuLianBot/discord-part/internal/storage"
	"github.com/LiuLianMagicBox/LiuLianBot/discord-part/internal/testdb"
	dg "github.com/bwmarrin/discordgo"
)

func toolboxFixture(t *testing.T) *tbBridge {
	t.Helper()
	path := filepath.Join(t.TempDir(), "config.json")
	config := tbConfig{Listen: "127.0.0.1:0", StateFile: "state.json", Users: map[string]tbGrant{"1": {Idle: true}, "2": {Idle: true, Mailbox: true, Email: "user@example.invalid"}}}
	config.SMTP.RelayInbox = "relay@example.invalid"
	writeTBConfig(t, path, config)
	s, err := newTB(path)
	if err != nil {
		t.Fatal(err)
	}
	s.voices = func(owners ...string) map[string]*tbVoice {
		result := map[string]*tbVoice{}
		for _, owner := range owners {
			result[owner] = &tbVoice{GuildID: "100", ChannelID: owner}
		}
		return result
	}
	return s
}

func writeTBConfig(t *testing.T, path string, c tbConfig) {
	t.Helper()
	raw, err := json.Marshal(c)
	if err != nil {
		t.Fatal(err)
	}
	if err = os.WriteFile(path, raw, 0600); err != nil {
		t.Fatal(err)
	}
}

func tbDeviceFor(t *testing.T, s *tbBridge, owner, name string) (*tbDevice, string) {
	t.Helper()
	code := s.issue(s.links, owner)
	reply, err := s.enroll(code, name)
	if err != nil {
		t.Fatal(err)
	}
	v := reply.(map[string]string)
	return s.data.Devices[v["id"]], v["token"]
}

func tbCall(s *tbBridge, route, token string, in tbRequest) (any, error) {
	req := httptest.NewRequest(http.MethodPost, "/v1/"+route, nil)
	req.Header.Set("Authorization", "Bearer "+token)
	return s.dispatch(req, route, in)
}

func TestToolboxPairingAndRevocation(t *testing.T) {
	s := toolboxFixture(t)
	a, ta := tbDeviceFor(t, s, "1", "A")
	b, tb := tbDeviceFor(t, s, "2", "B")
	expired := s.issue(s.invites, a.ID)
	s.invites[tbHash(expired)] = tbCode{a.ID, s.now().Add(-time.Second)}
	if _, err := tbCall(s, "pair/accept", tb, tbRequest{Code: expired}); err != tbError("InvalidCode") {
		t.Fatal(err)
	}
	code := s.issue(s.invites, a.ID)
	if _, err := tbCall(s, "pair/accept", ta, tbRequest{Code: code}); err != tbError("InvalidCode") {
		t.Fatal(err)
	}
	if _, err := tbCall(s, "pair/accept", tb, tbRequest{Code: code}); err != nil {
		t.Fatal(err)
	}
	if _, err := tbCall(s, "pair/accept", tb, tbRequest{Code: code}); err != tbError("InvalidCode") {
		t.Fatal(err)
	}
	if _, err := s.queue(a, b.ID, "player"); err != tbError("Offline") {
		t.Fatal(err)
	}
	if _, err := tbCall(s, "sync", tb, tbRequest{Name: "B", Apps: []tbApp{{"player", "Player"}}}); err != nil {
		t.Fatal(err)
	}
	job, err := s.queue(a, b.ID, "player")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := tbCall(s, "result", ta, tbRequest{ID: job.ID, Result: "Launched"}); err != tbError("Denied") {
		t.Fatal(err)
	}
	if _, err := tbCall(s, "pair/remove", tb, tbRequest{Target: a.ID}); err != nil {
		t.Fatal(err)
	}
	if _, err := s.queue(a, b.ID, "player"); err != tbError("Denied") {
		t.Fatal(err)
	}
	if _, err := tbCall(s, "unlink", tb, tbRequest{}); err != nil {
		t.Fatal(err)
	}
	if _, err := tbCall(s, "sync", tb, tbRequest{Name: "B"}); err != tbError("Unauthorized") {
		t.Fatal(err)
	}
	restart, err := newTB(s.configPath)
	if err != nil {
		t.Fatal(err)
	}
	if len(restart.data.Devices) != 1 || restart.data.Devices[a.ID].Peer != "" {
		t.Fatal("pair/revocation did not survive restart")
	}
	raw, _ := os.ReadFile(s.statePath)
	if bytes.Contains(raw, []byte(ta)) || bytes.Contains(raw, []byte(tb)) {
		t.Fatal("plain device token persisted")
	}
}

func TestToolboxAlarmTargetAndReceipt(t *testing.T) {
	s := toolboxFixture(t)
	a, ta := tbDeviceFor(t, s, "1", "A")
	b, tb := tbDeviceFor(t, s, "1", "B")
	other, _ := tbDeviceFor(t, s, "2", "C")
	call := func(route string, token string, in tbRequest) {
		t.Helper()
		if _, err := tbCall(s, route, token, in); err != nil {
			t.Fatal(err)
		}
	}
	call("sync", ta, tbRequest{Name: "A"})
	call("sync", tb, tbRequest{Name: "B"})
	due := time.Now().Add(time.Hour).UTC().Truncate(time.Second)
	args := map[string]string{"time": due.Format(time.RFC3339)}
	run := func() (string, error) { return s.slash(t.Context(), dg.EnglishUS, "1", "alarm", args, "") }
	if _, err := run(); err != tbError("ChooseDevice") {
		t.Fatal(err)
	}
	call("default", ta, tbRequest{Target: a.ID})
	args["pc"] = "B"
	msg, err := run()
	if err != nil || !strings.Contains(msg, "on B") || !strings.Contains(msg, "<t:") || len(a.Alarms) != 0 || len(b.Alarms) != 1 {
		t.Fatal(msg, err)
	}
	alarm := b.Alarms[0]
	if !alarm.Due.Equal(due) {
		t.Fatal(alarm)
	}
	delete(args, "pc")
	if _, err := run(); err != nil || len(a.Alarms) != 1 {
		t.Fatal(err)
	}
	if _, err := tbCall(s, "default", ta, tbRequest{Target: other.ID}); err != tbError("Denied") {
		t.Fatal(err)
	}
	// A device cannot acknowledge its sibling's alarm.
	call("sync", ta, tbRequest{Name: "A", Acks: []string{alarm.ID}})
	if len(b.Alarms) != 1 {
		t.Fatal("cross-device acknowledgement")
	}
	call("sync", tb, tbRequest{Name: "B", Acks: []string{alarm.ID}})
	if len(b.Alarms) != 0 {
		t.Fatal("acknowledged alarm still queued")
	}
	s.live[b.ID] = tbLive{Seen: time.Now().Add(-time.Minute)}
	args["pc"] = b.ID
	msg, err = run()
	if err != nil || !strings.Contains(msg, "B is offline") {
		t.Fatal(msg, err)
	}
	args["pc"] = other.ID
	if _, err := run(); err != tbError("ChooseDevice") {
		t.Fatal(err)
	}
}

func TestToolboxIdleAndMailRelay(t *testing.T) {
	s := toolboxFixture(t)
	d, token := tbDeviceFor(t, s, "2", "B")
	call := func(route string, in tbRequest) {
		t.Helper()
		if _, err := tbCall(s, route, token, in); err != nil {
			t.Fatal(err)
		}
	}
	call("sync", tbRequest{Name: "B", IdleSeconds: 601})
	call("idle", tbRequest{Enabled: true, Minutes: 10, Reply: "Back later"})
	call("mailbox", tbRequest{Enabled: true, Route: "dm"})
	contact := func() string {
		v, err := s.slash(t.Context(), dg.EnglishUS, "1", "contact", map[string]string{"user": "2"}, "999")
		if err != nil {
			t.Fatal(err)
		}
		return v
	}
	if !strings.Contains(contact(), "Back later") || !strings.Contains(contact(), "</toolbox message:999>") {
		t.Fatal(contact())
	}
	var relay tbRequest
	var to string
	s.sendMail = func(_ context.Context, _ tbConfig, e tbEnvelope) error {
		to = e.To
		relay = tbRequest{ID: e.ID, Proof: e.Proof, Body: e.Body}
		return nil
	}
	var delivered int
	s.deliverDM = func(_ context.Context, owner, body, nonce string) error {
		if owner != "2" || body != "Discord 1\n\nHello" || nonce != relay.ID {
			t.Fatal(owner, body)
		}
		delivered++
		return nil
	}
	if _, err := s.submitMail(t.Context(), dg.EnglishUS, "1", "2", "Hello"); err != nil {
		t.Fatal(err)
	}
	if delivered != 0 || to != "relay@example.invalid" || relay.Proof == "" {
		t.Fatal("DM bypassed MailServer")
	}
	tampered := relay
	tampered.Body = "different"
	if err := s.mailReceived(t.Context(), tampered); err != tbError("Denied") {
		t.Fatal(err)
	}
	for range 2 {
		if err := s.mailReceived(t.Context(), relay); err != nil {
			t.Fatal(err)
		}
	}
	if delivered != 1 || s.data.Receipts[relay.ID].Status != "DmDelivered" {
		t.Fatal("duplicate DM or missing receipt")
	}
	call("mailbox", tbRequest{Enabled: true, Route: "email"})
	if _, err := s.submitMail(t.Context(), dg.EnglishUS, "1", "2", "Email"); err != nil || to != "user@example.invalid" || relay.Proof == "" {
		t.Fatal(err, to)
	}
	if s.data.Receipts[relay.ID].Status != "MailAccepted" {
		t.Fatal("wrong SMTP receipt")
	}
	for _, result := range []string{"EmailDelayed", "EmailDelivered", "EmailDelayed"} {
		relay.Result = result
		if err := s.mailStatus(relay); err != nil {
			t.Fatal(err)
		}
	}
	if s.data.Receipts[relay.ID].Status != "EmailDelivered" {
		t.Fatal("late DSN overwrote delivery")
	}
	relay.Proof = "forged"
	if err := s.mailStatus(relay); err != tbError("Denied") {
		t.Fatal(err)
	}
	call("sync", tbRequest{Name: "B", IdleSeconds: 0})
	if strings.Contains(contact(), "Back later") {
		t.Fatal("replied while active")
	}
	if _, err := s.submitMail(t.Context(), dg.EnglishUS, "1", "2", "Hello"); err != tbError("Denied") {
		t.Fatal(err)
	}
	call("sync", tbRequest{Name: "B", IdleSeconds: 601})
	cfg, _ := tbSettings(s.configPath)
	delete(cfg.Users, "2")
	writeTBConfig(t, s.configPath, cfg)
	if strings.Contains(contact(), "Back later") {
		t.Fatal("revoked permission still active")
	}
	for _, route := range []string{"idle", "mailbox"} {
		if _, err := tbCall(s, route, token, tbRequest{}); err != tbError("Denied") {
			t.Fatal(route, err)
		}
	}
	if _, err := s.submitMail(t.Context(), dg.EnglishUS, "1", d.Owner, "Hi"); err != tbError("Denied") {
		t.Fatal(err)
	}
	st, err := tbCall(s, "sync", token, tbRequest{Name: "B"})
	if err != nil {
		t.Fatal(err)
	}
	view := st.(tbSnapshot)
	if view.Idle != nil || view.Mailbox != nil {
		t.Fatal("revoked menus still visible")
	}
}

func TestToolboxPrivateChannelTransferAndPermissions(t *testing.T) {
	db, _ := testdb.New(t)
	ctx := t.Context()
	if err := storage.Migrate(ctx, db); err != nil {
		t.Fatal(err)
	}
	b, f := setupBot(t, db)
	defer b.cancel()
	g := addGuild(t, b)
	// Retain the initially granted permissions in the gateway cache throughout.
	ch := *g.Channels[1]
	for _, id := range []string{"1", "2", "900"} {
		ch.PermissionOverwrites = append(ch.PermissionOverwrites, &dg.PermissionOverwrite{ID: id, Type: dg.PermissionOverwriteTypeMember, Allow: dg.PermissionManageChannels})
	}
	if err := b.S.State.ChannelAdd(&ch); err != nil {
		t.Fatal(err)
	}
	live := ch
	live.PermissionOverwrites = append([]*dg.PermissionOverwrite{}, ch.PermissionOverwrites...)
	live.Name = "Studio"
	live.UserLimit = 5
	if err := b.db.AddVoice(ctx, storage.Voice{Guild: "10", ID: "21", Owner: "1"}, "Studio"); err != nil {
		t.Fatal(err)
	}
	var patches int
	f.answer = func(c call) (int, string) {
		if c.method == "PATCH" {
			patches++
			if c.data["user_limit"] != float64(0) || c.data["name"] != "New studio" {
				t.Fatal(c.data)
			}
		}
		switch {
		case strings.HasSuffix(c.path, "/guilds/10"):
			return 200, `{"id":"10","owner_id":"3","roles":[{"id":"10","permissions":"0"}]}`
		case strings.Contains(c.path, "/members/"):
			id := c.path[strings.LastIndex(c.path, "/")+1:]
			return 200, `{"user":{"id":"` + id + `"},"roles":[]}`
		default:
			raw, _ := json.Marshal(live)
			return 200, string(raw)
		}
	}
	owned, err := b.toolboxChannels(ctx, "1")
	if err != nil || len(owned) != 1 || owned[0].Limit != 5 {
		t.Fatal(owned, err)
	}
	edit := tbPrivate{"10", "21", "New studio", 0}
	if err := b.toolboxEdit(ctx, "1", edit); err != nil {
		t.Fatal(err)
	}
	if _, err := db.Exec("UPDATE private_voice_channels SET owner_id='2' WHERE channel_id='21'"); err != nil {
		t.Fatal(err)
	}
	if err := b.toolboxEdit(ctx, "1", edit); err != tbError("Denied") {
		t.Fatal("old owner", err)
	}
	// Revoke the new owner's Discord permission after they opened the editor.
	live.PermissionOverwrites = append(live.PermissionOverwrites[:1], live.PermissionOverwrites[2:]...)
	if err := b.toolboxEdit(ctx, "2", edit); err != tbError("Denied") {
		t.Fatal("missing Discord permission", err)
	}
	if patches != 1 {
		t.Fatal("unexpected edit count", patches)
	}
}

func TestToolboxSlashRegistrationAndLink(t *testing.T) {
	s := toolboxFixture(t)
	t.Setenv("LIULIAN_TOOLBOX_CONFIG", s.configPath)
	b, f := setupBot(t, nil)
	defer b.cancel()
	f.answer = func(c call) (int, string) {
		if c.method == http.MethodPut {
			return 200, "[]"
		}
		return 200, `{"id":"700"}`
	}
	b.register("900")
	calls := f.take()
	if len(calls) != 1 || len(calls[0].list) != len(b.cat.list)+1 {
		t.Fatal("command missing from bulk registration")
	}
	b.interaction(interaction("toolbox", "1", &dg.ApplicationCommandInteractionDataOption{Name: "link", Type: dg.ApplicationCommandOptionSubCommand}))
	calls = f.take()
	if len(calls) != 2 || calls[0].data["data"].(map[string]any)["flags"] != float64(dg.MessageFlagsEphemeral) || len(b.toolbox.links) != 1 {
		t.Fatal("link code was not an ephemeral response")
	}
}

func TestToolboxDesktopClients(t *testing.T) {
	dotnet := os.Getenv("TOOLBOX_TEST_DOTNET")
	if dotnet == "" {
		t.Skip("pass --dotnet to run desktop contract tests")
	}
	s := toolboxFixture(t)
	cfg, _ := tbSettings(s.configPath)
	delete(cfg.Users, "2")
	writeTBConfig(t, s.configPath, cfg)
	a := s.issue(s.links, "1")
	b := s.issue(s.links, "2")
	mux := http.NewServeMux()
	mux.HandleFunc("/v1/", s.serve)
	mux.HandleFunc("POST /simulate-discord-alarm", func(w http.ResponseWriter, r *http.Request) {
		_, err := s.slash(r.Context(), dg.EnglishUS, "2", "alarm", map[string]string{"pc": "Beta", "time": time.Now().Add(time.Hour).Format(time.RFC3339)}, "999")
		if err != nil {
			http.Error(w, "alarm failed", 400)
			return
		}
		w.WriteHeader(204)
	})
	server := httptest.NewServer(mux)
	defer server.Close()
	cmd := exec.CommandContext(t.Context(), dotnet, "test", os.Getenv("TOOLBOX_TEST_PROJECT"), "-c", "Release", "--nologo")
	cmd.Env = append(os.Environ(), "TOOLBOX_TEST_URL="+server.URL, "TOOLBOX_TEST_LINK_A="+a, "TOOLBOX_TEST_LINK_B="+b)
	out, err := cmd.CombinedOutput()
	t.Logf("%s", out)
	if err != nil {
		t.Fatal(err)
	}
}

func TestToolboxVoiceSnapshotRequiresAnAvailableGateway(t *testing.T) {
	b, _ := setupBot(t, nil)
	defer b.cancel()
	g := addGuild(t, b)
	g.VoiceStates = []*dg.VoiceState{{UserID: "1", ChannelID: "21"}, {UserID: "2", ChannelID: "21"}}
	g.Channels[1].Name = "Studio"
	if err := b.S.State.GuildAdd(g); err != nil {
		t.Fatal(err)
	}
	if len(b.toolboxVoices("1", "2")) != 0 {
		t.Fatal("disconnected gateway supplied stale voice data")
	}
	b.S.DataReady = true
	v := b.toolboxVoices("1", "2")
	if len(v) != 2 || *v["1"] != *v["2"] || v["1"].ChannelName != "Studio" {
		t.Fatal(v)
	}
	other := &dg.Guild{ID: "11", VoiceStates: []*dg.VoiceState{{UserID: "1", ChannelID: "22"}}}
	if err := b.S.State.GuildAdd(other); err != nil {
		t.Fatal(err)
	}
	if v := b.toolboxVoices("1", "2"); v["1"] != nil || v["2"] == nil {
		t.Fatal("ambiguous state used for sharing")
	}
}

func TestToolboxMoveRequiresTheOwnersConsentAndJoinPermission(t *testing.T) {
	b, f := setupBot(t, nil)
	defer b.cancel()
	s := toolboxFixture(t)
	b.toolbox = s
	s.move = b.toolboxMove
	s.voices = b.toolboxVoices
	g := addGuild(t, b)
	dest := &dg.Channel{ID: "22", GuildID: "10", Name: "Studio", Type: dg.ChannelTypeGuildVoice}
	g.Channels = append(g.Channels, dest)
	g.VoiceStates = []*dg.VoiceState{{UserID: "1", ChannelID: "21"}, {UserID: "2", ChannelID: "22"}}
	if err := b.S.State.GuildAdd(g); err != nil {
		t.Fatal(err)
	}
	b.S.DataReady = true
	a, ta := tbDeviceFor(t, s, "1", "A")
	other, tb := tbDeviceFor(t, s, "2", "B")
	if _, err := tbCall(s, "pair/accept", tb, tbRequest{Code: s.issue(s.invites, a.ID)}); err != nil {
		t.Fatal(err)
	}
	if _, err := tbCall(s, "sync", tb, tbRequest{Name: "B"}); err != nil {
		t.Fatal(err)
	}

	destAllowed := true
	moves := 0
	f.answer = func(c call) (int, string) {
		switch {
		case c.method == "PATCH":
			if !strings.HasSuffix(c.path, "/guilds/10/members/1") || c.data["channel_id"] != "22" {
				t.Fatalf("moved someone other than the consenting member: %v", c)
			}
			moves++
			return 204, ""
		case strings.HasSuffix(c.path, "/guilds/10"):
			return 200, `{"id":"10","owner_id":"3","roles":[{"id":"10","permissions":"1049600"},{"id":"16","permissions":"8"}]}`
		case strings.HasSuffix(c.path, "/members/900"):
			return 200, `{"user":{"id":"900"},"roles":["16"]}`
		case strings.HasSuffix(c.path, "/members/1"):
			return 200, `{"user":{"id":"1"},"roles":[]}`
		default:
			ch := *dest
			if strings.HasSuffix(c.path, "/channels/21") {
				ch.ID = "21"
			}
			if ch.ID == "22" && !destAllowed {
				ch.PermissionOverwrites = []*dg.PermissionOverwrite{{ID: "1", Type: dg.PermissionOverwriteTypeMember, Deny: dg.PermissionVoiceConnect}}
			}
			raw, _ := json.Marshal(ch)
			return 200, string(raw)
		}
	}
	req := tbRequest{Target: other.ID, Source: "21", GuildID: "10", ChannelID: "22"}
	if _, err := tbCall(s, "voice/join", tb, req); err != tbError("Denied") {
		t.Fatal("peer submitted consent on behalf of target", err)
	}
	bad := req
	bad.Source = "20"
	if _, err := tbCall(s, "voice/join", ta, bad); err != tbError("VoiceFallback") {
		t.Fatal("stale source", err)
	}
	destAllowed = false
	if _, err := tbCall(s, "voice/join", ta, req); err != tbError("VoiceFallback") {
		t.Fatal("channel denied", err)
	}
	destAllowed = true
	dest.UserLimit = 1
	if _, err := tbCall(s, "voice/join", ta, req); err != tbError("VoiceFallback") {
		t.Fatal("channel full", err)
	}
	dest.UserLimit = 0
	if _, err := tbCall(s, "voice/join", ta, req); err != nil {
		t.Fatal(err)
	}
	g.VoiceStates[0].ChannelID = "22"
	if _, err := tbCall(s, "voice/join", ta, req); err != tbError("VoiceFallback") {
		t.Fatal("repeated a completed move", err)
	}
	if moves != 1 {
		t.Fatal("unexpected number of Discord moves", moves)
	}
}

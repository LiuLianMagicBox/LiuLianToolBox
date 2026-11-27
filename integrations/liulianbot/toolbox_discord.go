package bot

import (
	"context"
	embedfs "embed"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"slices"
	"strconv"
	"strings"
	"time"

	dg "github.com/bwmarrin/discordgo"
)

//go:embed toolbox_i18n/*.json
var tbResources embedfs.FS
var tbStrings = func() map[string]map[string]string {
	t := map[string]map[string]string{}
	for _, lang := range []string{"zh-TW", "zh-CN", "en"} {
		raw, err := tbResources.ReadFile("toolbox_i18n/" + lang + ".json")
		if err != nil {
			panic(err)
		}
		var v map[string]string
		if err := json.Unmarshal(raw, &v); err != nil {
			panic(err)
		}
		t[lang] = v
	}
	return t
}()

func tbT(lang dg.Locale, key string, args ...string) string {
	tag := "zh-TW"
	if strings.HasPrefix(string(lang), "en") {
		tag = "en"
	} else if lang == dg.ChineseCN {
		tag = "zh-CN"
	}
	msg := tbStrings[tag][key]
	if msg == "" {
		msg = tbStrings["zh-TW"][key]
	}
	if msg == "" {
		msg = tbStrings["zh-TW"]["Failed"]
	}
	var replacements []string
	for i, arg := range args {
		replacements = append(replacements, "{"+strconv.Itoa(i)+"}", arg)
	}
	return strings.NewReplacer(replacements...).Replace(msg)
}

func tbCommand() *dg.ApplicationCommand {
	localize := func(key string) map[dg.Locale]string {
		return map[dg.Locale]string{
			dg.EnglishUS: tbT(dg.EnglishUS, key), dg.ChineseTW: tbT(dg.ChineseTW, key), dg.ChineseCN: tbT(dg.ChineseCN, key)}
	}
	option := func(name, key string, t dg.ApplicationCommandOptionType, required bool) *dg.ApplicationCommandOption {
		return &dg.ApplicationCommandOption{Name: name, Type: t, Required: required, Description: tbT(dg.EnglishUS, key), DescriptionLocalizations: localize(key)}
	}
	sub := func(name, key string, opts ...*dg.ApplicationCommandOption) *dg.ApplicationCommandOption {
		o := option(name, key, dg.ApplicationCommandOptionSubCommand, false)
		o.Options = opts
		return o
	}
	pc := func(req bool) *dg.ApplicationCommandOption {
		return option("pc", "CmdPC", dg.ApplicationCommandOptionString, req)
	}
	user := func() *dg.ApplicationCommandOption {
		return option("user", "CmdUser", dg.ApplicationCommandOptionUser, true)
	}
	descs := localize("CmdToolbox")
	return &dg.ApplicationCommand{Name: "toolbox", Description: tbT(dg.EnglishUS, "CmdToolbox"), DescriptionLocalizations: &descs,
		Options: []*dg.ApplicationCommandOption{
			sub("link", "CmdLink"), sub("unlink", "CmdUnlink", pc(true)),
			sub("alarm", "CmdAlarm", option("time", "CmdTime", dg.ApplicationCommandOptionString, true), pc(false)),
			sub("contact", "CmdContact", user()), sub("message", "CmdMessage", user(), option("text", "CmdText", dg.ApplicationCommandOptionString, true)),
			sub("receipt", "CmdReceipt", option("id", "CmdReceiptID", dg.ApplicationCommandOptionString, true)),
		}}
}

func (b *Bot) initToolbox() error {
	path := os.Getenv("LIULIAN_TOOLBOX_CONFIG")
	if path == "" {
		return nil
	}
	s, err := newTB(path)
	if err != nil {
		return err
	}
	s.voices = b.toolboxVoices
	s.channels = b.toolboxChannels
	s.edit = b.toolboxEdit
	s.move = b.toolboxMove
	s.deliverDM = func(ctx context.Context, owner, body, nonce string) error {
		ch, err := b.S.UserChannelCreate(owner, dg.WithContext(ctx))
		if err != nil {
			return err
		}
		// Discord deduplicates a retried message with the same nonce in its retry window.
		endpoint := dg.EndpointChannelMessages(ch.ID)
		_, err = b.S.RequestWithBucketID("POST", endpoint, map[string]any{"content": body, "allowed_mentions": map[string]any{"parse": []string{}},
			"nonce": nonce, "enforce_nonce": true}, endpoint, dg.WithContext(ctx))
		return err
	}
	b.toolbox = s
	return nil
}

func (b *Bot) toolboxVoices(owners ...string) map[string]*tbVoice {
	locations := map[string]*tbVoice{}
	b.S.RLock()
	ready := b.S.DataReady
	b.S.RUnlock()
	if !ready {
		return locations
	}
	st := b.S.State
	st.RLock()
	defer st.RUnlock()
	// Read both accounts under one lock so a move can't split the snapshot.
	for _, guild := range st.Guilds {
		if guild == nil || guild.Unavailable {
			continue
		}
		for _, voice := range guild.VoiceStates {
			if voice == nil || voice.ChannelID == "" || !slices.Contains(owners, voice.UserID) {
				continue
			}
			if _, exists := locations[voice.UserID]; exists {
				// Wait for a consistent gateway state during a cross-guild move.
				locations[voice.UserID] = nil
				continue
			}
			location := &tbVoice{GuildID: guild.ID, ChannelID: voice.ChannelID, GuildName: guild.Name}
			for _, ch := range guild.Channels {
				if ch != nil && ch.ID == voice.ChannelID {
					location.ChannelName = ch.Name
					break
				}
			}
			locations[voice.UserID] = location
		}
	}
	return locations
}

func (b *Bot) toolboxChannels(ctx context.Context, owner string) ([]tbPrivate, error) {
	rows, err := b.db.QueryContext(ctx, "SELECT guild_id,channel_id FROM private_voice_channels WHERE owner_id=? AND config_type='private' ORDER BY guild_id,channel_id", owner)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := []tbPrivate{}
	for rows.Next() {
		var gid, cid string
		if err := rows.Scan(&gid, &cid); err != nil {
			return nil, err
		}
		ch, err := b.S.Channel(cid, dg.WithContext(ctx))
		if err != nil {
			if status(err) == 404 {
				continue
			}
			return nil, err
		}
		if ch.GuildID == gid && ch.Type == dg.ChannelTypeGuildVoice {
			result = append(result, tbPrivate{gid, cid, ch.Name, ch.UserLimit})
		}
	}
	return result, rows.Err()
}

func (b *Bot) toolboxEdit(ctx context.Context, owner string, v tbPrivate) error {
	b.voiceMu.Lock()
	defer b.voiceMu.Unlock()
	// Shares the lock with LiuLianBot's ownership transfer and channel cleanup.
	var owns bool
	err := b.db.QueryRowContext(ctx, "SELECT EXISTS(SELECT 1 FROM private_voice_channels WHERE guild_id=? AND channel_id=? AND owner_id=? AND config_type='private')", v.GuildID, v.ChannelID, owner).Scan(&owns)
	if err != nil {
		return err
	}
	if !owns {
		return tbError("Denied")
	}
	opt := dg.WithContext(ctx)
	ch, err := b.S.Channel(v.ChannelID, opt)
	if err != nil {
		return err
	}
	if ch.GuildID != v.GuildID || ch.Type != dg.ChannelTypeGuildVoice {
		return tbError("Denied")
	}
	ids := []string{owner, b.self()}
	permissions, err := b.toolboxPermissions(ctx, v.GuildID, []*dg.Channel{ch}, ids)
	if err != nil {
		return err
	}
	for _, id := range ids {
		perm, err := permissions.UserChannelPermissions(id, ch.ID)
		if err != nil || perm&dg.PermissionManageChannels == 0 {
			return tbError("Denied")
		}
	}
	// ChannelEdit omits zero, but zero explicitly removes a previous user limit.
	endpoint := dg.EndpointChannel(v.ChannelID)
	_, err = b.S.RequestWithBucketID("PATCH", endpoint, map[string]any{"name": v.Name, "user_limit": v.Limit}, endpoint, opt)
	if status(err) == 403 {
		return tbError("Denied")
	}
	return err
}

func (b *Bot) toolboxInteraction(i *dg.Interaction) {
	u := i.User
	if i.Member != nil {
		u = i.Member.User
	}
	if u == nil {
		return
	}
	// Acknowledge first so SMTP, Discord and disk I/O can take longer than three seconds.
	err := b.S.InteractionRespond(i, &dg.InteractionResponse{Type: dg.InteractionResponseDeferredChannelMessageWithSource,
		Data: &dg.InteractionResponseData{Flags: dg.MessageFlagsEphemeral}}, dg.WithContext(b.ctx))
	if err != nil {
		return
	}
	data := i.ApplicationCommandData()
	msg := tbT(i.Locale, "InvalidInput")
	if len(data.Options) == 1 {
		op := data.Options[0]
		args := map[string]string{}
		for _, o := range op.Options {
			args[o.Name] = fmt.Sprint(o.Value)
		}
		msg, err = b.toolbox.slash(b.ctx, i.Locale, u.ID, op.Name, args, data.ID)
		if err != nil {
			var e tbError
			if errors.As(err, &e) {
				msg = tbT(i.Locale, string(e))
			} else {
				report("ToolBox command", err)
				msg = tbT(i.Locale, "Failed")
			}
		}
	}
	_, err = b.S.InteractionResponseEdit(i, &dg.WebhookEdit{Content: &msg, AllowedMentions: &dg.MessageAllowedMentions{Parse: []dg.AllowedMentionType{}}}, dg.WithContext(b.ctx))
	report("ToolBox response", err)
}

func (s *tbBridge) resolve(owner, name string) string {
	if d := s.data.Devices[name]; d != nil && d.Owner == owner {
		return d.ID
	}
	var ids []string
	for id, d := range s.data.Devices {
		if d.Owner == owner && strings.EqualFold(d.Name, name) {
			ids = append(ids, id)
		}
	}
	if len(ids) == 1 {
		return ids[0]
	}
	return ""
}

func (s *tbBridge) slash(ctx context.Context, lang dg.Locale, owner, command string, args map[string]string, commandID string) (string, error) {
	if command == "message" {
		return s.submitMail(ctx, lang, owner, args["user"], args["text"])
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	switch command {
	case "link":
		return tbT(lang, "LinkIssued", s.issue(s.links, owner)), nil
	case "unlink":
		d := s.data.Devices[s.resolve(owner, args["pc"])]
		if d == nil {
			return "", tbError("ChooseDevice")
		}
		return tbT(lang, "Saved"), s.revoke(d)
	case "alarm":
		due, err := time.Parse(time.RFC3339, args["time"])
		if err != nil {
			return "", tbError("InvalidTime")
		}
		target := args["pc"]
		if target != "" {
			target = s.resolve(owner, target)
			if target == "" {
				return "", tbError("ChooseDevice")
			}
		}
		d, err := s.alarm(owner, target, due)
		if err == tbError("Offline") {
			return tbT(lang, "AlarmOffline", d.Name), nil
		}
		if err != nil {
			return "", err
		}
		return tbT(lang, "AlarmScheduled", d.Name, "<t:"+strconv.FormatInt(due.Unix(), 10)+":F>"), nil
	case "contact":
		c, err := tbSettings(s.configPath)
		if err != nil {
			return "", err
		}
		target := args["user"]
		g := c.Users[target]
		p := s.data.Users[target]
		if p == nil || !g.Idle || !s.isIdle(target, p.Idle) {
			return tbT(lang, "NoAutoReply"), nil
		}
		msg := p.Idle.Reply
		if g.Mailbox && p.Mailbox.Enabled {
			msg += "\n" + tbT(lang, "MailEntry", "</toolbox message:"+commandID+">", "<@"+target+">")
		}
		return msg, nil
	case "receipt":
		r := s.data.Receipts[args["id"]]
		if r == nil || !slices.Contains([]string{r.Sender, r.Owner}, owner) {
			return "", tbError("Denied")
		}
		return tbT(lang, r.Status), nil
	}
	return "", tbError("InvalidInput")
}

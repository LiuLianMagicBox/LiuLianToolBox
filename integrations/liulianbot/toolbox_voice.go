package bot

import (
	"context"
	"time"

	dg "github.com/bwmarrin/discordgo"
)

// Load current roles and channel overwrites before a privileged Discord action.
// GuildAdd lets DiscordGo apply the same permission algorithm as its live cache.
func (b *Bot) toolboxPermissions(ctx context.Context, gid string, channels []*dg.Channel, users []string) (*dg.State, error) {
	opt := dg.WithContext(ctx)
	guild, err := b.S.Guild(gid, opt)
	if err != nil {
		return nil, err
	}
	guild.Channels = channels
	guild.Members = nil
	for _, id := range users {
		member, err := b.S.GuildMember(gid, id, opt)
		if err != nil {
			return nil, tbError("Denied")
		}
		if member.CommunicationDisabledUntil != nil && member.CommunicationDisabledUntil.After(time.Now()) {
			return nil, tbError("Denied")
		}
		guild.Members = append(guild.Members, member)
	}
	st := dg.NewState()
	err = st.GuildAdd(guild)
	return st, err
}

// The device consents only to moving its own already-connected account from the
// displayed source to the paired account's exact destination.
func (s *tbBridge) moveOwner(device string, in tbRequest) (string, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	local := s.data.Devices[device]
	peer := s.data.Devices[in.Target]
	if local == nil || peer == nil || local.Peer != peer.ID || peer.Peer != local.ID || local.Owner == peer.Owner || !s.online(peer.ID) {
		return "", tbError("Denied")
	}
	voices := s.voices(local.Owner, peer.Owner)
	from, to := voices[local.Owner], voices[peer.Owner]
	if from == nil || to == nil || from.GuildID != in.GuildID || to.GuildID != in.GuildID || from.ChannelID != in.Source ||
		to.ChannelID != in.ChannelID || from.ChannelID == to.ChannelID {
		return "", tbError("VoiceFallback")
	}
	return local.Owner, nil
}

func (b *Bot) toolboxMove(ctx context.Context, device string, in tbRequest) error {
	owner, err := b.toolbox.moveOwner(device, in)
	if err != nil {
		return err
	}
	opt := dg.WithContext(ctx)
	source, err := b.S.Channel(in.Source, opt)
	if err != nil {
		return tbError("VoiceFallback")
	}
	dest, err := b.S.Channel(in.ChannelID, opt)
	if err != nil {
		return tbError("VoiceFallback")
	}
	if source.GuildID != in.GuildID || dest.GuildID != in.GuildID || source.Type != dg.ChannelTypeGuildVoice || dest.Type != dg.ChannelTypeGuildVoice {
		return tbError("VoiceFallback")
	}
	botID := b.self()
	perms, err := b.toolboxPermissions(ctx, in.GuildID, []*dg.Channel{source, dest}, []string{owner, botID})
	if err != nil {
		return tbError("VoiceFallback")
	}
	has := func(user, channel string, need int64) bool {
		got, err := perms.UserChannelPermissions(user, channel)
		return err == nil && got&need == need
	}
	const join = dg.PermissionViewChannel | dg.PermissionVoiceConnect
	if !has(owner, dest.ID, join) || !has(botID, source.ID, dg.PermissionVoiceMoveMembers) || !has(botID, dest.ID, join|dg.PermissionVoiceMoveMembers) {
		return tbError("VoiceFallback")
	}
	if dest.UserLimit > 0 {
		g := b.guild(dest.GuildID)
		if g == nil || g.Unavailable {
			return tbError("VoiceFallback")
		}
		occupants := 0
		for _, v := range g.VoiceStates {
			if v.ChannelID == dest.ID {
				occupants++
			}
		}
		if occupants >= dest.UserLimit {
			return tbError("VoiceFallback")
		}
	}
	// Recheck after the REST reads in case either user moved or the pair was revoked.
	if _, err = b.toolbox.moveOwner(device, in); err != nil {
		return err
	}
	if err = b.S.GuildMemberMove(in.GuildID, owner, &in.ChannelID, opt); err != nil {
		return tbError("VoiceFallback")
	}
	return nil
}

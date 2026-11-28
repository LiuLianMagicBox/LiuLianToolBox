using System.Diagnostics;

namespace LiuLian.ToolBox.Desktop;

internal sealed partial class MainForm
{
    private ConnectionOverview overview = null!;
    private SectionCard guidanceCard = null!;
    private TableLayoutPanel launchRow = null!;
    private FlowLayoutPanel setupActions = null!;
    private Label launchHelp = null!, hintLabel = null!;
    private Label? lastLabel, resultTime;
    private ComboBox remoteApps = null!;
    private ToolButton launchButton = null!, setupButton = null!, joinButton = null!, moveButton = null!, sourceButton = null!;

    private void HomePage()
    {
        overview = new ConnectionOverview(colors, text, smallFont, nameFont) { Margin = Gap(15) };
        AddRow(fields, overview);

        guidanceCard = Card("VoiceChannel", Glyph.Sound);
        hintLabel = Note(guidanceCard, "", false);
        var guidance = Actions(guidanceCard);
        joinButton = ButtonFor(guidance, "Join", Glyph.Arrow, () =>
        {
            if (hints.Join is { Valid: true } channel)
                using (Process.Start(new ProcessStartInfo(channel.Link.AbsoluteUri) { UseShellExecute = true })) { }
            return Task.CompletedTask;
        });
        moveButton = ButtonFor(guidance, "ConfirmMove", Glyph.Check, async () =>
        {
            if (hints.Join is not { } target || state?.Voice is not { } current || state.Peer is null)
                throw new BotError("VoiceFallback");
            await Change("voice/join", new { target = state.Peer.Id, source = current.ChannelId, target.GuildId, target.ChannelId });
        }, ButtonKind.Primary);
        sourceButton = ButtonFor(guidance, "Source", Glyph.Monitor, () => { Navigate("Current"); return Task.CompletedTask; });

        var launch = Card("RemoteLaunch", Glyph.Apps);
        launchHelp = Note(launch, "");
        launchRow = Table(); Columns(launchRow, new(SizeType.Percent, 100), new(SizeType.AutoSize));
        remoteApps = new ToolCombo(colors) {
            AccessibleName = text["LaunchApp"], Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, D(12), 0) };
        launchRow.Controls.Add(remoteApps, 0, 0);
        var send = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        launchRow.Controls.Add(send, 1, 0); AddRow(launch, launchRow);
        launchButton = ButtonFor(send, "Launch", Glyph.Play, async () =>
        {
            var target = state?.Peer ?? throw new BotError("Offline");
            if (!target.Online) throw new BotError("Offline");
            var app = Selected(remoteApps);
            if (app == "") throw new BotError("AppMissing");
            await Change("launch", new { target = target.Id, app });
        }, ButtonKind.Primary);
        setupActions = Actions(launch);
        setupButton = ButtonFor(setupActions, "Link", Glyph.Arrow, async () =>
        {
            if (state is null) Navigate("Bot");
            else if (state.Peer is null) Navigate("Remote");
            else await Poll();
        }, ButtonKind.Primary);

        var last = Card("LastAction", Glyph.History);
        var row = Table(); Columns(row, new(SizeType.Percent, 100), new(SizeType.AutoSize));
        lastLabel = new Label { AutoSize = true, Dock = DockStyle.Fill, UseMnemonic = false, Margin = Padding.Empty, Tag = Tone.Feedback };
        resultTime = new Label { AutoSize = true, Font = smallFont, Tag = Tone.Muted, Margin = new Padding(D(12), 0, 0, 0) };
        row.Controls.Add(lastLabel, 0, 0); row.Controls.Add(resultTime, 1, 0); AddRow(last, row);
        UpdateHome();
    }

    private void UpdateHome()
    {
        if (overview is null || overview.IsDisposed) return;
        var peer = state?.Peer;
        var hasCredentials = settings.ProtectedToken.Length > 0;
        var remoteStatus = peer is null ? text[state is null && hasCredentials ? "UnknownStatus" : "UnpairedStatus"]
            : text[peer.Online ? "Online" : "OfflineStatus"];
        overview.UpdateDevices(settings.Name, peer?.Name, ConnectionStatus, remoteStatus,
            state is null ? "" : LocationText(state.Voice), peer?.Online == true ? LocationText(peer.Voice) : "",
            state is not null, peer is not null, peer?.Online == true);

        var canLaunch = peer is { Online: true, Apps.Length: > 0 };
        Choices(remoteApps, peer?.Apps.Select(a => new Choice(a.Id, a.Name)) ?? [], Selected(remoteApps));
        launchButton.Available = canLaunch;
        launchRow.Visible = canLaunch;
        launchHelp.Visible = setupActions.Visible = !canLaunch;
        if (!canLaunch)
        {
            var (reason, action) = (state, peer, hasCredentials) switch
            {
                (null, _, false) => ("LinkForLaunch", "Link"),
                (null, _, true) => (connectionKey, "FixConnection"),
                (_, null, _) => ("PairForLaunch", "OpenPairing"),
                (_, { Online: false }, _) => ("PeerOfflineHelp", "Refresh"),
                _ => ("NoRemoteApps", "Refresh")
            };
            launchHelp.Text = text[reason];
            setupButton.Text = setupButton.AccessibleName = text[action];
        }
        guidanceCard.Visible = hints.Join is not null || hints.Share is not null;
        joinButton.Visible = hints.Join is not null;
        moveButton.Visible = hints.Join is { } target && state?.Voice is { Valid: true } local && target.GuildId == local.GuildId;
        sourceButton.Visible = hints.Share == "Reselect";
        hintLabel.Text = hints.Join is not null ? text.Format("JoinHint", LocationText(hints.Join)) : hints.Share is null ? "" : ShareText();
    }
}

using System.Diagnostics;

namespace LiuLian.ToolBox.Desktop;

internal sealed partial class MainForm
{
    private Label? localLabel, peerLabel, lastLabel, hintLabel;
    private ComboBox? remoteApps;
    private Button? launchButton, joinButton, moveButton;

    private void HomePage()
    {
        LabelFor("LocalPC"); localLabel = Note("");
        LabelFor("PeerPC"); peerLabel = Note("");
        LabelFor("LaunchApp"); remoteApps = Combo();
        launchButton = ButtonFor("Launch", async () =>
        {
            var target = state?.Peer ?? throw new BotError("Offline");
            var app = Selected(remoteApps);
            if (!target.Online) throw new BotError("Offline");
            if (app == "") throw new BotError("AppMissing");
            await Change("launch", new { target = target.Id, app });
        });
        hintLabel = Note("");
        joinButton = ButtonFor("Join", () =>
        {
            if (hints.Join is { Valid: true } channel)
                using (Process.Start(new ProcessStartInfo(channel.Link.AbsoluteUri) { UseShellExecute = true })) { }
            return Task.CompletedTask;
        });
        moveButton = ButtonFor("ConfirmMove", async () =>
        {
            if (hints.Join is not { } target || state?.Voice is not { } current || state.Peer is null)
                throw new BotError("VoiceFallback");
            await Change("voice/join", new { target = state.Peer.Id, source = current.ChannelId, target.GuildId, target.ChannelId });
        });
        LabelFor("LastAction"); lastLabel = Note(text.Format(lastKey, lastArgs));
        UpdateHome();
    }
    private void UpdateHome()
    {
        if (localLabel is null || peerLabel is null || remoteApps is null || launchButton is null ||
            joinButton is null || moveButton is null || hintLabel is null) return;
        localLabel.Text = settings.Name + " · " + text[connectionKey] +
            (state is null ? "" : Environment.NewLine + LocationText(state.Voice));
        var peer = state?.Peer;
        peerLabel.Text = peer is null ? text[state is null ? connectionKey : "NotPaired"] :
            peer.Name + " · " + text[peer.Online ? "Online" : "Offline"] +
                (peer.Online ? Environment.NewLine + LocationText(peer.Voice) : "");
        Choices(remoteApps, peer?.Apps.Select(a => new Choice(a.Id, a.Name)) ?? [], Selected(remoteApps));
        launchButton.Enabled = peer?.Online == true && remoteApps.Items.Count > 0;
        joinButton.Visible = hints.Join is not null;
        moveButton.Visible = hints.Join is { } target && state?.Voice is { Valid: true } current && target.GuildId == current.GuildId;
        hintLabel.Text = hints.Join is not null ? text.Format("JoinHint", LocationText(hints.Join)) :
            hints.Share is not null ? ShareText() : "";
        hintLabel.Visible = hintLabel.Text.Length > 0;
    }

    private void CurrentPage()
    {
        LabelFor("PCName"); var name = Input(settings.Name, 64);
        LabelFor("Language"); var language = Combo();
        Choices(language, [new("ZH_TW", "繁體中文"), new("ZH_CN", "简体中文"), new("EN", "English")], settings.Language);
        LabelFor("LocalApps"); var apps = Combo();
        var editing = settings.Apps.ToList();
        void ShowApps() => Choices(apps, editing.Select(a => new Choice(a.Id, a.Name)), Selected(apps));
        ShowApps();
        ButtonFor("AddApp", () =>
        {
            using var dialog = new OpenFileDialog { Filter = text["AppFilter"] + "|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                if (!Path.GetExtension(dialog.FileName).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    throw new BotError("InvalidInput");
                if (editing.Count >= 128) throw new BotError("TooMany");
                if (!editing.Any(a => string.Equals(a.Path, dialog.FileName, StringComparison.OrdinalIgnoreCase)))
                {
                    var label = Path.GetFileNameWithoutExtension(dialog.FileName);
                    editing.Add(new(Guid.NewGuid().ToString("N"), label[..Math.Min(100, label.Length)], dialog.FileName));
                }
                ShowApps();
            }
            return Task.CompletedTask;
        });
        ButtonFor("Remove", () => { editing.RemoveAll(a => a.Id == Selected(apps)); ShowApps(); return Task.CompletedTask; });
        LabelFor("Source"); var sources = Combo();
        List<ShareSource> available = [];
        void LoadSources(string? id)
        {
            available = WindowsEnvironment.Sources(text);
            Choices(sources, new[] { new Choice("", text["ChooseSource"]) }
                .Concat(available.Select(s => new Choice(s.Id, s.Name))), id);
        }
        LoadSources(settings.Source?.Id);
        ButtonFor("Refresh", () => { LoadSources(Selected(sources)); return Task.CompletedTask; });
        ButtonFor("Save", async () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) throw new BotError("InvalidInput");
            settings.Name = name.Text.Trim(); settings.Language = Selected(language);
            settings.Source = available.Find(s => s.Id == Selected(sources)); settings.Apps = editing;
            Save(); text = new(settings.Language); Report("Saved"); Render(); await Poll();
        });
    }

    private void RemotePage()
    {
        var follow = Check("Follow", settings.Follow);
        follow.CheckedChanged += (_, _) =>
        {
            try { settings.Follow = follow.Checked; Save(); SetState(state); Report("Saved"); }
            catch (Exception ex) when (Recoverable(ex)) { Report(Error(ex)); }
        };
        if (state is null) { Note(text[connectionKey]); return; }
        if (state.Peer is null)
        {
            Note(text["PairHelp"]);
            LabelFor("PairCode"); var code = Input("", 64);
            ButtonFor("CreateInvite", async () =>
            {
                var invite = await client!.Post<Invite>("pair/invite", new { }, closing.Token);
                if (!code.IsDisposed) { code.Text = invite.Code; code.SelectAll(); code.Focus(); }
            });
            ButtonFor("Pair", () => Change("pair/accept", new { code = code.Text.Trim() }));
            return;
        }
        var peer = state.Peer;
        LabelFor("PeerPC"); Note(peer.Name + " · " + text.Format("DeviceID", peer.Id));
        var share = Check("ShareEnabled", state.Presenter != "");
        LabelFor("Presenter"); var presenter = Combo();
        Choices(presenter, [new(state.Id, settings.Name), new(peer.Id, peer.Name)], state.Presenter);
        presenter.Enabled = share.Checked;
        share.CheckedChanged += (_, _) => presenter.Enabled = share.Checked;
        ButtonFor("Save", () => Change("pair/share", new { id = peer.Id, target = share.Checked ? Selected(presenter) : "" }));
        ButtonFor("Unpair", () => Change("pair/remove", new { target = peer.Id }));
    }

    private void BotPage()
    {
        if (settings.ProtectedToken.Length == 0)
        {
            Note(text["LinkHelp"]);
            LabelFor("BotUrl"); var url = Input(settings.Endpoint, 1000);
            LabelFor("LinkCode"); var code = Input("", 64); code.UseSystemPasswordChar = true;
            ButtonFor("Link", async () =>
            {
                var address = url.Text.Trim();
                var candidate = new BotClient(address, null);
                using (candidate)
                {
                    var credentials = await candidate.Post<Credentials>("link", new { code = code.Text.Trim(), settings.Name }, closing.Token);
                    settings.ProtectedToken = WindowsEnvironment.Protect(credentials.Token);
                    settings.DeviceId = credentials.Id; settings.Endpoint = address;
                    Save(); client = new(address, credentials.Token); connectionKey = "BotUnavailable";
                    Report("Saved"); Render(); await Poll();
                }
            });
            return;
        }
        Note(settings.Endpoint); Note(text.Format("DeviceID", settings.DeviceId));
        if (state is null) Note(text[connectionKey]);
        ButtonFor("Unlink", async () =>
        {
            if (client is null) throw new BotError("Unauthorized");
            await client.Post<Reply>("unlink", new { }, closing.Token); Forget();
        });
        if (state is null)
        {
            ButtonFor("Forget", () => { Forget(); return Task.CompletedTask; });
            return;
        }
        LabelFor("DefaultPC"); var devices = Combo();
        Choices(devices, new[] { new Choice("", text["None"]) }.Concat(state.Devices.Select(d =>
            new Choice(d.Id, d.Name + " · " + d.Id))), state.DefaultDevice);
        ButtonFor("Save", () => Change("default", new { target = Selected(devices) }));
        LabelFor("PrivateChannels"); var channels = Combo();
        LabelFor("ChannelName"); var name = Input("", 100);
        LabelFor("UserLimit"); var limit = Row(new NumericUpDown { Maximum = 99, Width = 120 });
        PrivateChannel[] owned = [];
        channels.SelectedIndexChanged += (_, _) =>
        {
            var ch = owned.FirstOrDefault(c => c.ChannelId == Selected(channels));
            name.Text = ch?.Name ?? ""; limit.Value = ch?.Limit ?? 0;
        };
        ButtonFor("LoadChannels", async () =>
        {
            var loaded = await client!.Post<PrivateChannel[]>("channels", new { }, closing.Token);
            if (channels.IsDisposed) return;
            owned = loaded;
            Choices(channels, owned.Select(c => new Choice(c.ChannelId, c.Name + " · " + c.ChannelId)));
            if (owned.Length == 0) Report("NoChannels");
        });
        ButtonFor("Save", () =>
        {
            var ch = owned.FirstOrDefault(c => c.ChannelId == Selected(channels)) ?? throw new BotError("InvalidInput");
            return Change("channel", new { ch.GuildId, ch.ChannelId, name = name.Text.Trim(), limit = (int)limit.Value });
        });
    }
    private void Forget()
    {
        client?.Dispose(); client = null; alarmAcks = []; settings.ProtectedToken = ""; settings.DeviceId = "";
        settings.Results.Clear(); settings.Alarms.Clear(); Save(); connectionKey = "NotLinked";
        SetState(null); Report("Saved"); Render();
    }

    private void IdlePage()
    {
        var idle = state!.Idle!;
        var enabled = Check("IdleEnabled", idle.Enabled);
        LabelFor("IdleMinutes"); var minutes = Row(new NumericUpDown { Minimum = 1, Maximum = 1440, Value = idle.Minutes });
        LabelFor("ReplyText"); var reply = Input(idle.Reply, 1000); reply.Multiline = true; reply.Height = 90;
        ButtonFor("Save", () => Change("idle", new { enabled = enabled.Checked, minutes = (int)minutes.Value, reply = reply.Text }));
    }

    private void MailPage()
    {
        var mail = state!.Mailbox!;
        var enabled = Check("MailEnabled", mail.Enabled);
        LabelFor("Route"); var route = Combo();
        Choices(route, [new("dm", text["DM"]), new("email", text["Email"])], mail.Route);
        ButtonFor("Save", () => Change("mailbox", new { enabled = enabled.Checked, route = Selected(route) }));
    }
}

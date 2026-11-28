using System.Runtime.InteropServices;

namespace LiuLian.ToolBox.Desktop;

internal sealed partial class MainForm
{
    private ComboBox? defaultDevices, presenterChoice;
    private SwitchBox? shareSwitch;

    private void CurrentPage()
    {
        var device = Card("DevicePreferences", Glyph.Settings);
        var name = Input(device, "PCName", settings.Name, 64);
        var (left, right) = FieldPair(device);
        var language = Combo(left, "Language");
        Choices(language, [new("ZH_TW", "繁體中文"), new("ZH_CN", "简体中文"), new("EN", "English")], settings.Language);
        var appearance = Combo(right, "Appearance");
        Choices(appearance, [new("System", text["SystemTheme"]), new("Light", text["LightTheme"]), new("Dark", text["DarkTheme"])], settings.Appearance);

        var allowed = Card("AllowedApps", Glyph.Apps);
        var empty = Note(allowed, text["NoLocalApps"]);
        var editing = settings.Apps.ToList();
        var apps = new ListBox { AccessibleName = text["LocalApps"], Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle,
            DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false, ItemHeight = Math.Max(D(46), Font.Height + smallFont.Height + D(9)),
            DisplayMember = nameof(LocalApp.Name), Margin = Gap(6) };
        apps.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= apps.Items.Count) return;
            var app = (LocalApp)apps.Items[e.Index]!;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            var fg = selected ? colors.AccentInk : colors.Ink;
            using var bg = new SolidBrush(selected ? colors.Soft : colors.Field);
            e.Graphics.FillRectangle(bg, e.Bounds);
            var x = e.Bounds.Left + D(10);
            var width = Math.Max(1, e.Bounds.Width - D(20));
            const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(e.Graphics, app.Name, boldFont, new Rectangle(x, e.Bounds.Top + D(2), width, boldFont.Height + D(2)), fg, flags);
            TextRenderer.DrawText(e.Graphics, app.Path, smallFont, new Rectangle(x, e.Bounds.Top + boldFont.Height + D(4), width, smallFont.Height + D(3)), selected ? fg : colors.Muted, flags);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        };
        AddRow(allowed, apps);
        var bar = Actions(allowed);
        ToolButton? remove = null;
        void ShowApps(string? id)
        {
            apps.BeginUpdate(); apps.Items.Clear(); apps.Items.AddRange(editing.Cast<object>().ToArray());
            var index = editing.FindIndex(a => a.Id == id);
            apps.SelectedIndex = index >= 0 ? index : editing.Count == 0 ? -1 : 0;
            apps.Height = editing.Count == 0 ? 0 : Math.Min(3, editing.Count) * apps.ItemHeight + D(3);
            apps.Margin = editing.Count == 0 ? Padding.Empty : Gap(6);
            apps.Visible = editing.Count > 0; empty.Visible = editing.Count == 0; apps.EndUpdate();
            if (remove is not null) remove.Available = apps.SelectedIndex >= 0;
        }
        ButtonFor(bar, "AddApp", Glyph.Plus, () =>
        {
            using var dialog = new OpenFileDialog { Filter = text["AppFilter"] + "|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return Task.CompletedTask;
            if (!Path.GetExtension(dialog.FileName).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new BotError("InvalidInput");
            var added = editing.Find(a => string.Equals(a.Path, dialog.FileName, StringComparison.OrdinalIgnoreCase));
            if (added is null)
            {
                if (editing.Count >= 128) throw new BotError("TooMany");
                var label = Path.GetFileNameWithoutExtension(dialog.FileName);
                added = new(Guid.NewGuid().ToString("N"), label[..Math.Min(100, label.Length)], dialog.FileName);
                editing.Add(added);
            }
            ShowApps(added.Id); return Task.CompletedTask;
        });
        remove = ButtonFor(bar, "Remove", Glyph.Remove, () =>
        {
            if (apps.SelectedItem is LocalApp app) editing.Remove(app);
            ShowApps(null); return Task.CompletedTask;
        }, ButtonKind.Danger);
        apps.SelectedIndexChanged += (_, _) => remove.Available = apps.SelectedIndex >= 0;
        ShowApps(null);

        var sharing = Card("Source", Glyph.Share);
        var sources = Combo(sharing, "Source", false);
        List<ShareSource> available = [];
        void LoadSources(string? id)
        {
            available = WindowsEnvironment.Sources(text);
            var choices = new List<Choice> { new("", text["ChooseSource"]) };
            choices.AddRange(available.Select(s => new Choice(s.Id, s.Name)));
            if (settings.Source is { } saved && !available.Exists(s => s.Id == saved.Id))
                choices.Add(new(saved.Id, text.Format("UnavailableSource", saved.Name)));
            Choices(sources, choices, id);
        }
        LoadSources(settings.Source?.Id);
        ButtonFor(Actions(sharing), "Refresh", Glyph.Refresh, () => { LoadSources(Selected(sources)); return Task.CompletedTask; });
        SaveAction(async () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { name.Focus(); throw new BotError("InvalidInput"); }
            settings.Name = name.Text.Trim(); settings.Language = Selected(language); settings.Appearance = Selected(appearance);
            var selected = Selected(sources);
            settings.Source = available.Find(s => s.Id == selected) ?? (settings.Source?.Id == selected ? settings.Source : null);
            settings.Apps = editing;
            Save(); text = new(settings.Language); colors.Select(settings.Appearance); Report("Saved"); Render(); await Poll();
        });
    }

    private void RemotePage()
    {
        var pair = Card("Pairing", Glyph.Pair);
        if (state is null)
        {
            Note(pair, text[connectionKey]);
            var action = settings.ProtectedToken.Length == 0 ? "Link" : "FixConnection";
            ButtonFor(Actions(pair), action, Glyph.Arrow, () => { Navigate("Bot"); return Task.CompletedTask; }, ButtonKind.Primary);
        }
        else if (state.Peer is null)
        {
            Note(pair, text["PairHelp"]);
            var (receive, issue) = FieldPair(pair);
            var code = Input(receive, "PairCode", "", 64);
            var accept = ButtonFor(Actions(receive), "Pair", Glyph.Link, () => Change("pair/accept", new { code = code.Text.Trim() }), ButtonKind.Primary);
            accept.Available = false;
            code.TextChanged += (_, _) => accept.Available = !string.IsNullOrWhiteSpace(code.Text);
            var generated = Input(issue, "LocalPairCode", "", 64);
            generated.ReadOnly = true;
            var bar = Actions(issue);
            ButtonFor(bar, "CreateInvite", Glyph.Plus, async () =>
            {
                var invite = await client!.Post<Invite>("pair/invite", new { }, closing.Token);
                if (!generated.IsDisposed) { generated.Text = invite.Code; generated.SelectAll(); generated.Focus(); }
            });
            var copy = ButtonFor(bar, "CopyCode", Glyph.Copy, () =>
            {
                try { Clipboard.SetText(generated.Text); Report("Copied"); }
                catch (ExternalException) { throw new BotError("CopyFailed"); }
                return Task.CompletedTask;
            });
            copy.Available = false;
            generated.TextChanged += (_, _) => copy.Available = generated.Text.Length > 0;
        }
        else
        {
            var peer = state.Peer;
            Note(pair, peer.Name, false); Note(pair, text.Format("DeviceID", peer.Id));
            ButtonFor(Actions(pair), "Unpair", Glyph.Unlink, () => Change("pair/remove", new { target = peer.Id }), ButtonKind.Danger);
        }

        var voice = Card("VoiceFollow", Glyph.Sound);
        var follow = Check(voice, "Follow", settings.Follow);
        follow.CheckedChanged += (_, _) =>
        {
            try { settings.Follow = follow.Checked; Save(); SetState(state); Report("Saved"); }
            catch (Exception ex) when (Recoverable(ex)) { Report(Error(ex)); }
        };
        if (state?.Peer is not { } paired) return;
        var sharing = Card("ScreenSharing", Glyph.Share);
        var share = shareSwitch = Check(sharing, "ShareEnabled", state.Presenter != "");
        var presenter = presenterChoice = Combo(sharing, "Presenter");
        Choices(presenter, [new(state.Id, settings.Name), new(paired.Id, paired.Name)], state.Presenter);
        presenter.Enabled = share.Checked;
        share.CheckedChanged += (_, _) => presenter.Enabled = share.Checked;
        ButtonFor(Actions(sharing), "Save", Glyph.Check, () => Change("pair/share", new { id = paired.Id, target = share.Checked ? Selected(presenter) : "" }), ButtonKind.Primary);
    }

    private void UpdatePresenter(Snapshot? old, Snapshot? next)
    {
        if (shareSwitch is null || presenterChoice is null || next?.Peer is not { } peer || old is null) return;
        bool edited = shareSwitch.Checked != (old.Presenter != "") || (shareSwitch.Checked && Selected(presenterChoice) != old.Presenter);
        if (!edited && old.Presenter != next.Presenter) shareSwitch.Checked = next.Presenter != "";
        var selected = edited ? Selected(presenterChoice) : next.Presenter;
        Choices(presenterChoice, [new(next.Id, settings.Name), new(peer.Id, peer.Name)], selected);
    }

    private void BotPage()
    {
        var bot = Card("BotConnection", Glyph.Bot);
        if (settings.ProtectedToken.Length == 0)
        {
            Note(bot, text["LinkHelp"]);
            var code = Input(bot, "LinkCode", "", 64); code.UseSystemPasswordChar = true;
            var link = ButtonFor(Actions(bot), "Link", Glyph.Link, async () =>
            {
                using var candidate = new BotClient();
                var credentials = await candidate.Post<Credentials>("link", new { code = code.Text.Trim(), settings.Name }, closing.Token);
                settings.LinkDevice(credentials.Id, WindowsEnvironment.Protect(credentials.Token));
                try { Save(); }
                catch { settings.ForgetDevice(); throw; }
                client = new(credentials.Token); connectionKey = "BotUnavailable";
                Report("Saved"); Render(); await Poll();
            }, ButtonKind.Primary);
            link.Available = false;
            code.TextChanged += (_, _) => link.Available = !string.IsNullOrWhiteSpace(code.Text);
            return;
        }
        Note(bot, BotClient.ServiceUrl, false); Note(bot, text.Format("DeviceID", settings.DeviceId));
        if (state is null) Note(bot, text[connectionKey]);
        var bar = Actions(bot);
        ButtonFor(bar, "Unlink", Glyph.Unlink, async () =>
        {
            if (client is null) throw new BotError("Unauthorized");
            await client.Post<Reply>("unlink", new { }, closing.Token); Forget();
        }, ButtonKind.Danger);
        if (state is null)
        {
            ButtonFor(bar, "Forget", Glyph.Remove, () => { Forget(); return Task.CompletedTask; }, ButtonKind.Danger);
            return;
        }
        var alarm = Card("DefaultPC", Glyph.Bell);
        var devices = defaultDevices = Combo(alarm, "DefaultPC", false); UpdateDefaultDevice(null, state);
        ButtonFor(Actions(alarm), "Save", Glyph.Check, () => Change("default", new { target = Selected(devices) }), ButtonKind.Primary);
        var channel = Card("PrivateChannels", Glyph.Sound);
        var channels = Combo(channel, "PrivateChannels", false);
        var (left, right) = FieldPair(channel);
        var name = Input(left, "ChannelName", "", 100); var limit = Number(right, "UserLimit", 0, 99, 0);
        var buttons = Actions(channel);
        PrivateChannel[] owned = [];
        var save = ButtonFor(buttons, "Save", Glyph.Check, () =>
        {
            var ch = owned.FirstOrDefault(c => c.ChannelId == Selected(channels)) ?? throw new BotError("InvalidInput");
            return Change("channel", new { ch.GuildId, ch.ChannelId, name = name.Text.Trim(), limit = (int)limit.Value });
        }, ButtonKind.Primary);
        void SetChannel()
        {
            var ch = owned.FirstOrDefault(c => c.ChannelId == Selected(channels));
            name.Enabled = limit.Enabled = ch is not null; save.Available = ch is not null;
            name.Text = ch?.Name ?? ""; limit.Value = ch?.Limit ?? 0;
        }
        channels.SelectedIndexChanged += (_, _) => SetChannel(); SetChannel();
        ButtonFor(buttons, "LoadChannels", Glyph.Refresh, async () =>
        {
            var loaded = await client!.Post<PrivateChannel[]>("channels", new { }, closing.Token);
            if (channels.IsDisposed) return;
            owned = loaded;
            Choices(channels, owned.Select(c => new Choice(c.ChannelId, c.Name + " · " + c.ChannelId)), Selected(channels));
            SetChannel();
            if (owned.Length == 0) Report("NoChannels");
        });
    }

    private void UpdateDefaultDevice(Snapshot? previous, Snapshot? next)
    {
        if (defaultDevices is null || next is null) return;
        if (previous is not null && previous.Devices.SequenceEqual(next.Devices) && previous.DefaultDevice == next.DefaultDevice) return;
        var selected = previous is not null && Selected(defaultDevices) != previous.DefaultDevice ? Selected(defaultDevices) : next.DefaultDevice;
        Choices(defaultDevices, new[] { new Choice("", text["None"]) }.Concat(next.Devices.Select(d => new Choice(d.Id, d.Name + " · " + d.Id))), selected);
    }

    private void Forget()
    {
        client?.Dispose(); client = null; alarmAcks = []; settings.ForgetDevice();
        Save(); connectionKey = "NotLinked";
        SetState(null); Report("Saved"); Render();
    }

    private void IdlePage()
    {
        var idle = state!.Idle!;
        var card = Card("AutoReply", Glyph.Moon);
        var enabled = Check(card, "IdleEnabled", idle.Enabled);
        var minutes = Number(card, "IdleMinutes", 1, 1440, idle.Minutes);
        var reply = Input(card, "ReplyText", idle.Reply, 1000); reply.Multiline = true; reply.Height = D(88);
        SaveAction(() => Change("idle", new { enabled = enabled.Checked, minutes = (int)minutes.Value, reply = reply.Text }));
    }

    private void MailPage()
    {
        var mail = state!.Mailbox!;
        var card = Card("Route", Glyph.Mail);
        var enabled = Check(card, "MailEnabled", mail.Enabled);
        var route = Combo(card, "Route", false);
        Choices(route, [new("dm", text["DM"]), new("email", text["Email"])], mail.Route);
        SaveAction(() => Change("mailbox", new { enabled = enabled.Checked, route = Selected(route) }));
    }
}

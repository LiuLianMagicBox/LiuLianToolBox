using System.Diagnostics;
using System.Media;
using System.Security.Cryptography;
using System.Text.Json;

namespace LiuLian.ToolBox.Desktop;

internal sealed partial class MainForm : Form
{
    private readonly SettingsStore store;
    private readonly Settings settings;
    private readonly DeviceActions actions;
    private readonly VoiceGuide guide = new();
    private readonly CancellationTokenSource closing = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = "LiuLianToolBox", Visible = true };
    private readonly FlowLayoutPanel nav = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
        WrapContents = false, AutoScroll = true, Padding = new Padding(10, 20, 4, 0), BackColor = Color.FromArgb(33, 48, 56) };
    private readonly TableLayoutPanel fields = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(22, 12, 24, 18) };
    private readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0), AutoEllipsis = true };
    private readonly Panel scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
    private Catalog text;
    private BotClient? client;
    private Snapshot? state;
    private Guidance hints = new(null, null, false, false);
    private ShareSource? liveSource;
    private string page = "Home", lastKey = "NoActions", connectionKey = "NotLinked";
    private object?[] lastArgs = [];
    private string[] alarmAcks = [];
    private bool polling;
    private DateTime nextPoll;

    public MainForm(SettingsStore store, Settings settings)
    {
        this.store = store; this.settings = settings;
        text = new(settings.Language);
        actions = new(settings, Save, DeviceActions.OpenProgram);
        Text = "LiuLianToolBox";
        Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(970, 650);
        MinimumSize = new Size(780, 510);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 224));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 36));
        nav.Margin = scroll.Margin = Padding.Empty;
        scroll.Controls.Add(fields);
        layout.Controls.Add(nav, 0, 0); layout.Controls.Add(scroll, 1, 0);
        layout.Controls.Add(status, 0, 1); layout.SetColumnSpan(status, 2);
        Controls.Add(layout);
        if (settings.ProtectedToken.Length > 0)
        {
            try
            {
                client = new(settings.Endpoint, WindowsEnvironment.Unprotect(settings.ProtectedToken));
                connectionKey = "BotUnavailable";
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or BotError) { connectionKey = "Unauthorized"; }
        }
        tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        timer.Tick += async (_, _) => await Tick();
        Shown += async (_, _) => { timer.Start(); await Tick(); };
        Render();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop(); timer.Dispose(); closing.Cancel(); client?.Dispose();
        tray.Visible = false; tray.Dispose(); closing.Dispose();
        base.OnFormClosed(e);
    }

    private void Save() => store.Save(settings);
    private void Report(string key, params object?[] args)
    {
        lastKey = key; lastArgs = args; status.Text = text.Format(key, args);
        if (page == "Home" && lastLabel is not null) lastLabel.Text = status.Text;
    }

    private async Task Tick()
    {
        if (closing.IsCancellationRequested) return;
        try
        {
            foreach (var alarm in actions.TakeDue(DateTimeOffset.UtcNow))
            {
                Report("AlarmRing", settings.Name, alarm.Due.ToLocalTime().ToString("g"));
                SystemSounds.Exclamation.Play(); Notify(status.Text);
            }
            if (!polling && client is not null && DateTime.UtcNow >= nextPoll) await Poll();
        }
        catch (Exception ex) when (Recoverable(ex)) { if (!IsDisposed) Report(Error(ex)); }
    }

    private async Task Poll()
    {
        var api = client;
        if (polling || api is null) return;
        polling = true;
        try
        {
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
            heartbeat.CancelAfter(TimeSpan.FromSeconds(7));
            var snapshot = await api.Post<Snapshot>("sync", new { settings.Name,
                apps = settings.Apps.Select(a => new AppEntry(a.Id, a.Name)).ToArray(),
                idleSeconds = WindowsEnvironment.IdleSeconds(), acks = alarmAcks }, heartbeat.Token);
            if (client != api || closing.IsCancellationRequested) return;
            if (snapshot.Id != settings.DeviceId) throw new BotError("Unauthorized");
            connectionKey = "Online";
            SetState(snapshot);
            alarmAcks = actions.AcceptAlarms(snapshot.Alarms);
            actions.ForgetAcknowledgedAlarms(alarmAcks);
            foreach (var job in snapshot.Launches)
            {
                var result = actions.Receive(snapshot, job, DateTimeOffset.UtcNow);
                Report(result);
                await api.Post<Reply>("result", new { job.Id, result }, closing.Token);
            }
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            if (client == api && !closing.IsCancellationRequested)
            {
                connectionKey = Error(ex);
                SetState(null);
                if (connectionKey == "Unauthorized") { client = null; api.Dispose(); }
            }
        }
        finally { nextPoll = DateTime.UtcNow.AddSeconds(1); polling = false; }
    }

    private void SetState(Snapshot? next)
    {
        var old = state;
        state = next;
        var source = settings.Source;
        var canShare = next?.Peer is { Online: true, SameAccount: false, Voice.Valid: true } peer &&
            peer.Voice.SamePlace(next.Voice) && next.Presenter == next.Id;
        liveSource = canShare && source is not null ? WindowsEnvironment.Sources(text).Find(x => x.Id == source.Id) : null;
        hints = guide.Update(next, settings.Follow, source, liveSource is not null);
        if (hints.NotifyJoin) Notify(text.Format("JoinHint", LocationText(hints.Join)));
        if (hints.NotifyShare) Notify(ShareText());
        var menuChanged = !Catalog.Menu(old).SequenceEqual(Catalog.Menu(next));
        if (menuChanged || (old is null) != (next is null) ||
            (page == "Remote" && (old?.Peer?.Id != next?.Peer?.Id || old?.Presenter != next?.Presenter)) ||
            (page == "Bot" && old is not null && next is not null &&
            (!old.Devices.SequenceEqual(next.Devices) || old.DefaultDevice != next.DefaultDevice)))
            Render();
        else if (page == "Home") UpdateHome();
    }
    private string LocationText(Channel? channel) => channel is { Valid: true }
        ? text.Format("VoiceLocation", channel.GuildName ?? channel.GuildId, channel.ChannelName ?? channel.ChannelId) : text["NotInVoice"];
    private string ShareText() => hints.Share == "GoLive" ? text.Format("GoLive", liveSource?.Name ?? settings.Source?.Name) : text["Reselect"];
    private void Notify(string message) => tray.ShowBalloonTip(5000, "LiuLianToolBox", message, ToolTipIcon.Info);

    private static bool Recoverable(Exception ex) => ex is BotError or HttpRequestException or IOException or
        UnauthorizedAccessException or OperationCanceledException or JsonException or CryptographicException or
        System.ComponentModel.Win32Exception or ObjectDisposedException;
    private static string Error(Exception ex) => ex switch
    {
        BotError err => err.Code,
        IOException or UnauthorizedAccessException or CryptographicException => "SettingsError",
        System.ComponentModel.Win32Exception => "LaunchFailed",
        _ => "BotUnavailable"
    };

    private async Task Do(Button sender, Func<Task> run)
    {
        sender.Enabled = false;
        try { await run(); }
        catch (Exception ex) when (Recoverable(ex))
        {
            if (!closing.IsCancellationRequested) Report(Error(ex));
            if (ex is BotError { Code: "Denied" or "Unauthorized" } && client is not null) await Poll();
        }
        finally { if (!sender.IsDisposed) sender.Enabled = true; }
    }

    private async Task Change(string route, object body)
    {
        if (client is null) throw new BotError("NotLinked");
        Reply reply;
        try { reply = await client.Post<Reply>(route, body, closing.Token); }
        catch (Exception ex) when (route == "launch" && ex is HttpRequestException or OperationCanceledException)
        { throw new BotError("Uncertain"); }
        Report(reply.Code);
        await Poll();
    }

    private void Render()
    {
        if (!Catalog.Menu(state).Contains(page)) page = "Home";
        SuspendLayout(); fields.SuspendLayout(); nav.SuspendLayout();
        Clear(nav); Clear(fields); fields.RowStyles.Clear(); fields.RowCount = 0;
        var brand = new Label { Text = "LiuLianToolBox", Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.White, Size = new Size(198, 38), Padding = new Padding(7, 4, 0, 0) };
        nav.Controls.Add(brand);
        foreach (var key in Catalog.Menu(state))
        {
            var item = new Button { Text = text[key], Width = 195, Height = 39, FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6,0,0,0), ForeColor = Color.White,
                BackColor = key == page ? Color.FromArgb(54, 95, 98) : nav.BackColor, UseMnemonic = false };
            item.FlatAppearance.BorderSize = 0;
            item.Click += (_, _) => { page = key; Render(); };
            nav.Controls.Add(item);
        }
        var title = Note(text[page]); title.Font = new Font(Font.FontFamily, 19, FontStyle.Bold);
        switch (page)
        {
            case "Home": HomePage(); break;
            case "Current": CurrentPage(); break;
            case "Remote": RemotePage(); break;
            case "Bot": BotPage(); break;
            case "Idle": IdlePage(); break;
            case "Mailbox": MailPage(); break;
            case "Plugins": Note(text["PluginWip"]); break;
            case "Info": Note(text.Format("Version", Application.ProductVersion.Split('+')[0])); break;
        }
        status.Text = text.Format(lastKey, lastArgs);
        nav.ResumeLayout(); fields.ResumeLayout(); ResumeLayout(); scroll.AutoScrollPosition = Point.Empty;
    }
    private static void Clear(Control parent)
    {
        foreach (var c in parent.Controls.Cast<Control>().ToArray()) c.Dispose();
    }
    private T Row<T>(T control) where T : Control
    {
        control.Margin = new Padding(3, 4, 3, 5);
        fields.Controls.Add(control, 0, fields.RowCount++); return control;
    }
    private Label Note(string value) => Row(new Label { Text = value, AutoSize = true, MaximumSize = new Size(620,0), UseMnemonic = false });
    private void LabelFor(string key)
    {
        var label = Note(text[key]); label.Font = new Font(Font, FontStyle.Bold); label.Margin = new Padding(3, 12, 3, 2);
    }
    private TextBox Input(string value = "", int max = 250) => Row(new TextBox { Text = value, MaxLength = max, Dock = DockStyle.Top });
    private CheckBox Check(string key, bool value) => Row(new CheckBox { Text = text[key], Checked = value, AutoSize = true });
    private Button ButtonFor(string key, Func<Task> work)
    {
        var b = Row(new Button { Text = text[key], AutoSize = true, Padding = new Padding(8, 3, 8, 3) });
        b.Click += async (_, _) => await Do(b, work); return b;
    }
    private ComboBox Combo() => Row(new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top });
    private sealed record Choice(string Id, string Label) { public override string ToString() => Label; }
    private static void Choices(ComboBox box, IEnumerable<Choice> choices, string? selected = null)
    {
        var items = choices.ToArray();
        if (!box.Items.Cast<Choice>().SequenceEqual(items))
        {
            box.BeginUpdate(); box.Items.Clear(); box.Items.AddRange(items); box.EndUpdate();
        }
        var index = Array.FindIndex(items, i => i.Id == selected);
        box.SelectedIndex = index >= 0 ? index : items.Length == 0 ? -1 : 0;
    }
    private static string Selected(ComboBox box) => (box.SelectedItem as Choice)?.Id ?? "";
}

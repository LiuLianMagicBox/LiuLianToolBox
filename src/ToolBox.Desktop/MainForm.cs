using System.Diagnostics;
using System.Media;
using System.Security.Cryptography;
using System.Text.Json;

namespace LiuLian.ToolBox.Desktop;

internal sealed partial class MainForm : Form
{
    private readonly SettingsStore store;
    private readonly Font desktopFont;
    private readonly Icon appIcon = new(typeof(MainForm), "Assets.ToolBox.ico");
    private readonly Settings settings;
    private readonly DeviceActions actions;
    private readonly VoiceGuide guide = new();
    private readonly CancellationTokenSource closing = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly NotifyIcon tray = new() { Text = "LiuLianToolBox" };
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
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Font = desktopFont = DesktopFont(settings.Language);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = appIcon;
        tray.Icon = Icon; tray.Visible = true;
        if (settings.ProtectedToken.Length > 0)
        {
            try
            {
                client = new(WindowsEnvironment.Unprotect(settings.ProtectedToken));
                connectionKey = "BotUnavailable";
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or BotError) { connectionKey = "Unauthorized"; }
        }
        tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        timer.Tick += async (_, _) => await Tick();
        Shown += async (_, _) => { timer.Start(); await Tick(); };
    }

    private static Font DesktopFont(string language)
    {
        var preferred = language == "ZH_CN" ? "Microsoft YaHei UI" : "Microsoft JhengHei UI";
        foreach (var name in new[] { preferred, "Noto Sans CJK TC", "WenQuanYi Zen Hei" })
        {
            try
            {
                using var family = new FontFamily(name);
                if (family.IsStyleAvailable(FontStyle.Regular)) return new Font(family, 10.5f);
            }
            catch (ArgumentException) { }
        }
        return new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 10.5f);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var work = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(D(650), work.Width), Math.Min(D(450), work.Height));
        ClientSize = new Size(D(1060), D(696));
        Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
        CenterToScreen();
        BuildShell();
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
        lastKey = key; lastArgs = args; lastAt = DateTimeOffset.Now;
        feedbackPage = page;
        UpdateFeedback();
    }

    private async Task Tick()
    {
        if (closing.IsCancellationRequested) return;
        try
        {
            foreach (var alarm in actions.TakeDue(DateTimeOffset.UtcNow))
            {
                Report("AlarmRing", settings.Name, alarm.Due.ToLocalTime().ToString("g"));
                SystemSounds.Exclamation.Play(); Notify(text.Format(lastKey, lastArgs));
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
        var newMenu = Catalog.Menu(next).ToArray();
        if (!newMenu.Contains(page)) { page = "Home"; Render(); return; }
        if (!Catalog.Menu(old).SequenceEqual(newMenu)) BuildNavigation();
        UpdateSidebar();
        switch (page)
        {
            case "Home": UpdateHome(); break;
            case "Remote" when (old is null) != (next is null) || old?.Peer?.Id != next?.Peer?.Id:
            case "Bot" when (old is null) != (next is null):
                Render(); break;
            case "Remote": UpdatePresenter(old, next); break;
            case "Bot": UpdateDefaultDevice(old, next); break;
        }
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

    private async Task Do(ToolButton sender, Func<Task> run)
    {
        sender.Busy = true;
        try { await run(); }
        catch (Exception ex) when (Recoverable(ex))
        {
            if (!closing.IsCancellationRequested) Report(Error(ex));
            if (ex is BotError { Code: "Denied" or "Unauthorized" } && client is not null) await Poll();
        }
        finally { if (!sender.IsDisposed) sender.Busy = false; }
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

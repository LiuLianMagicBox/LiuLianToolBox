using System.Text.Json;
using LiuLian.ToolBox;
using Xunit;

namespace ToolBox.Tests;

public class DeviceTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("toolbox-tests-").FullName;
    private static readonly Channel Voice = new("100", "200");
    private static Snapshot Session(Channel? remote = null) =>
        new("a", "Alpha", Voice, new("b", "Beta", true, false, remote, []), "a",
            [], "", null, null, [], []);
    public void Dispose() => Directory.Delete(dir, true);

    [Fact]
    public void ExistingSettingsFollowWindowsUntilAnAppearanceIsSaved()
    {
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, """{"name":"Studio PC","language":"EN","apps":[{"id":"player","name":"Music","path":"player.exe"}]}""");
        var store = new SettingsStore(path);
        var settings = store.Load();
        Assert.Equal("System", settings.Appearance);
        settings.Appearance = "Dark";
        store.Save(settings);

        var saved = store.Load();
        Assert.Equal("Dark", saved.Appearance);
        Assert.Equal("Studio PC", saved.Name);
        Assert.Equal("EN", saved.Language);
        Assert.Equal("player", Assert.Single(saved.Apps).Id);
    }

    [Fact]
    public void LanguageResourcesAndNavigationMatchTheSpec()
    {
        var asm = typeof(Catalog).Assembly;
        Dictionary<string,string> Read(string tag)
        {
            using var stream = asm.GetManifestResourceStream($"LiuLian.ToolBox.Locales.{tag}.json");
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream!)!;
        }
        var traditional = Read("zh-TW");
        foreach (var lang in new[] { "zh-CN", "en" })
        {
            var translated = Read(lang);
            Assert.Equal(traditional.Keys.Order(), translated.Keys.Order());
            foreach (var (key, value) in translated)
            {
                Assert.False(string.IsNullOrWhiteSpace(value));
                Assert.Equal(System.Text.CompositeFormat.Parse(traditional[key]).MinimumArgumentCount,
                    System.Text.CompositeFormat.Parse(value).MinimumArgumentCount);
            }
        }
        Assert.Equal("首頁", new Catalog("unknown")["Home"]);
        Assert.Equal("首页", new Catalog("ZH_CN")["Home"]);
        Assert.Equal(new[] { "Home", "Current", "Remote", "Bot", "Plugins", "Info" }, Catalog.Menu(null));
        var state = Session() with { Idle = new(false, 10, ""), Mailbox = new(false, "dm") };
        Assert.Equal(new[] { "Home", "Current", "Remote", "Bot", "Idle", "Mailbox", "Plugins", "Info" }, Catalog.Menu(state));
        Assert.DoesNotContain("Idle", Catalog.Menu(state with { Idle = null }));
        Assert.Contains("Mailbox", Catalog.Menu(state with { Idle = null }));
    }

    [Fact]
    public void FollowAndSharingUseBothIdsAndOnlyPromptOnTransitions()
    {
        var guide = new VoiceGuide();
        var otherGuild = new Channel("101", "200");
        var state = Session(otherGuild);
        var source = new ShareSource("window1", "Player");
        var first = guide.Update(state, true, source, true);
        Assert.Equal(otherGuild, first.Join);
        Assert.True(first.NotifyJoin);
        Assert.Null(first.Share);
        Assert.False(guide.Update(state, true, source, true).NotifyJoin);
        Assert.Null(guide.Update(state, false, source, true).Join);
        state = Session(Voice);
        var same = guide.Update(state, true, source, true);
        Assert.Null(same.Join);
        Assert.Equal("GoLive", same.Share);
        Assert.True(same.NotifyShare);
        Assert.False(guide.Update(state, true, source, true).NotifyShare);
        var renamed = state with { Voice = Voice with { ChannelName = "New name" } };
        Assert.False(guide.Update(renamed, true, source, true).NotifyShare);
        Assert.Equal("GoLive", guide.Update(renamed, true, source, true).Share);
        Assert.Equal("Reselect", guide.Update(state, true, source, false).Share);
        Assert.Null(guide.Update(state with { Presenter = "b" }, true, source, true).Share);
        Assert.Null(guide.Update(state with { Peer = state.Peer! with { SameAccount = true } }, true, source, true).Share);
        Assert.Null(guide.Update(state with { Peer = state.Peer! with { Online = false } }, true, source, true).Share);
        Assert.True(guide.Update(state, true, source, true).NotifyShare);
        Assert.Null(guide.Update(null, true, source, true).Join);
        Assert.False(new Channel("100", "200/../../../").Valid);
    }

    [Fact]
    public void OnlyThePairedSenderCanLaunchALocallyListedProgramOnce()
    {
        var marker = Path.Combine(dir, "player.exe");
        File.WriteAllText(marker, "");
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        var s = store.Load();
        Assert.Equal("ZH_TW", s.Language);
        s.LinkDevice("a", "protected-test-token");
        s.Apps.Add(new("player", "Music", marker));
        var opened = new List<string>();
        var actions = new DeviceActions(s, () => store.Save(s), opened.Add);
        var job = new Launch("request", "b", "a", "player");
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("Denied", actions.Receive(Session(), job with { From = "c" }, now));
        Assert.Equal("Denied", actions.Receive(Session(), job with { Target = "b" }, now));
        Assert.Equal("AppMissing", actions.Receive(Session(), job with { App = marker }, now));
        Assert.Equal("Launched", actions.Receive(Session(), job, now));
        var reloaded = store.Load();
        var restarted = new DeviceActions(reloaded, () => store.Save(reloaded), opened.Add);
        Assert.Equal("Launched", restarted.Receive(Session(), job, now));
        Assert.Equal(new[] { marker }, opened);
        File.Delete(marker);
        Assert.Equal("AppMissing", restarted.Receive(Session(), job with { Id = "second" }, now));
    }

    [Fact]
    public void AlarmIsStoredBeforeAckAndRingsOnceAcrossRestarts()
    {
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        var s = store.Load();
        s.LinkDevice("a", "protected-test-token");
        var actions = new DeviceActions(s, () => store.Save(s), _ => { });
        var now = DateTimeOffset.UtcNow;
        var alarm = new Alarm("alarm-1", now.AddMinutes(1));
        Assert.Equal(new[] { alarm.Id }, actions.AcceptAlarms([alarm, alarm]));
        Assert.Empty(actions.TakeDue(now));
        s = store.Load(); actions = new(s, () => store.Save(s), _ => { });
        Assert.Equal(new[] { alarm }, actions.TakeDue(alarm.Due));
        actions.AcceptAlarms([alarm]);
        Assert.Empty(actions.TakeDue(alarm.Due));
        actions.ForgetAcknowledgedAlarms([alarm.Id]);
        Assert.True(store.Load().Alarms[alarm.Id].Fired);
        actions.ForgetAcknowledgedAlarms([]);
        Assert.Empty(store.Load().Alarms);
    }

    [Fact]
    public void DiskFailureDoesNotAcknowledgeAnAlarmOrStartAProgram()
    {
        var s = new Settings();
        s.LinkDevice("a", "protected-test-token");
        var app = Path.Combine(dir, "player.exe");
        File.WriteAllText(app, "");
        s.Apps.Add(new("player", "Player", app));
        var fail = true;
        var opened = 0;
        var actions = new DeviceActions(s, () => { if (fail) throw new IOException(); }, _ => opened++);
        var now = DateTimeOffset.UtcNow;
        var alarm = new Alarm("alarm", now);
        Assert.Throws<IOException>(() => actions.AcceptAlarms([alarm]));
        Assert.Empty(s.Alarms);
        var job = new Launch("job", "b", "a", "player");
        Assert.Throws<IOException>(() => actions.Receive(Session(), job, now));
        Assert.Equal(0, opened);
        fail = false;
        Assert.Equal("Launched", actions.Receive(Session(), job, now));
        actions.AcceptAlarms([alarm]);
        fail = true;
        Assert.Throws<IOException>(() => actions.TakeDue(now));
        fail = false;
        Assert.Equal(alarm, Assert.Single(actions.TakeDue(now)));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("https://other.example/toolbox", false)]
    [InlineData("http://127.0.0.1:48750", false)]
    [InlineData("http://www.liulian.dev/toolbox", false)]
    [InlineData("https://www.liulian.dev:444/toolbox", false)]
    [InlineData("https://www.liulian.dev/Toolbox", false)]
    [InlineData("https://www.liulian.dev/toolbox?test", false)]
    [InlineData("https://www.liulian.dev/toolbox#test", false)]
    [InlineData("https://test@www.liulian.dev/toolbox", false)]
    [InlineData("https://www.liulian.dev@other.example/toolbox", false)]
    [InlineData("https://www.liulian.dev/toolbox", true)]
    [InlineData("https://WWW.LIULIAN.DEV:443/toolbox/", true)]
    public void UpgradeKeepsDeviceDataOnlyForTheOfficialService(string? endpoint, bool keep)
    {
        var path = Path.Combine(dir, "settings.json");
        var old = new Dictionary<string, object> {
            ["name"] = "Studio", ["language"] = "EN", ["appearance"] = "Dark", ["follow"] = true,
            ["source"] = new { id = "screen:1", name = "Screen" },
            ["apps"] = new[] { new { id = "music", name = "Music", path = "music.exe" } },
            ["deviceId"] = "pc", ["protectedToken"] = "old-token",
            ["results"] = new { job = new { at = DateTimeOffset.UnixEpoch, code = "Launched" } },
            ["alarms"] = new { morning = new { alarm = new { id = "morning", due = DateTimeOffset.UnixEpoch }, fired = false } }
        };
        if (endpoint is not null) old["endpoint"] = endpoint;
        File.WriteAllText(path, JsonSerializer.Serialize(old));
        var store = new SettingsStore(path);
        foreach (var settings in new[] { store.Load(), store.Load() })
        {
            Assert.Equal("Studio", settings.Name);
            Assert.Equal("EN", settings.Language);
            Assert.Equal("Dark", settings.Appearance);
            Assert.True(settings.Follow);
            Assert.Equal("screen:1", settings.Source!.Id);
            Assert.Equal("music", Assert.Single(settings.Apps).Id);
            Assert.Equal(keep ? "old-token" : "", settings.ProtectedToken);
            Assert.Equal(keep ? "pc" : "", settings.DeviceId);
            Assert.Equal(keep, settings.Alarms.ContainsKey("morning"));
            Assert.Equal(keep, settings.Results.ContainsKey("job"));
        }
        if (!keep) Assert.DoesNotContain("old-token", File.ReadAllText(path));
    }
}

using LiuLian.ToolBox;
using Xunit;

namespace ToolBox.Tests;

public class BrokerTests
{
    private sealed class BrokerFactAttribute : FactAttribute
    {
        public BrokerFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("TOOLBOX_TEST_URL") is null)
                Skip = "Run integrations/liulianbot/build.py with --test --dotnet to start the relay fixture.";
        }
    }

    [BrokerFact]
    public async Task TwoDevicesPairLaunchAndReceiveOnlyTheirOwnAlarms()
    {
        var address = Environment.GetEnvironmentVariable("TOOLBOX_TEST_URL")!;
        using var signup = new BotClient(address, null);
        async Task<Credentials> Enroll(string name, string key) => await signup.Post<Credentials>("link",
            new { name, code = Environment.GetEnvironmentVariable(key) });
        var alice = await Enroll("Alpha", "TOOLBOX_TEST_LINK_A");
        var bob = await Enroll("Beta", "TOOLBOX_TEST_LINK_B");
        using var a = new BotClient(address, alice.Token);
        using var b = new BotClient(address, bob.Token);
        var invite = await a.Post<Invite>("pair/invite", new { });
        await b.Post<Reply>("pair/accept", new { invite.Code });
        async Task<Snapshot> Sync(BotClient pc, string name, AppEntry[] apps) =>
            await pc.Post<Snapshot>("sync", new { name, apps, idleSeconds = 0, acks = Array.Empty<string>() });

        AppEntry[] apps = [new("player", "Music player")];
        await Sync(b, "Beta", apps);
        var sa = await Sync(a, "Alpha", []);
        Assert.Equal(bob.Id, sa.Peer!.Id);
        Assert.Equal("Music player", Assert.Single(sa.Peer.Apps).Name);
        Assert.NotEqual(sa.Voice, sa.Peer.Voice);
        Assert.NotNull(sa.Idle);
        Assert.Null(sa.Mailbox);
        var refused = await Assert.ThrowsAsync<BotError>(() => a.Post<Reply>("mailbox", new { enabled = true, route = "dm" }));
        Assert.Equal("Denied", refused.Code);
        var noApp = await Assert.ThrowsAsync<BotError>(() => a.Post<Reply>("launch", new { target = bob.Id, app = "unlisted" }));
        Assert.Equal("AppMissing", noApp.Code);

        var response = a.Post<Reply>("launch", new { target = bob.Id, app = "player" });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        Snapshot sb;
        do
        {
            await Task.Delay(30, timeout.Token);
            sb = await Sync(b, "Beta", apps);
        } while (sb.Launches.Length == 0);
        var job = Assert.Single(sb.Launches);
        Assert.Null(sb.Idle);
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.SystemDirectory, "where.exe") : "/bin/true";
        var local = new Settings { DeviceId = bob.Id, Apps = [new("player", "Music player", executable)] };
        var launches = 0;
        var receiver = new DeviceActions(local, () => { }, path => { DeviceActions.OpenProgram(path); launches++; });
        var result = receiver.Receive(sb, job, DateTimeOffset.UtcNow);
        Assert.Equal("Launched", receiver.Receive(sb, job, DateTimeOffset.UtcNow)); // Lost acknowledgment
        Assert.Equal(1, launches);
        await b.Post<Reply>("result", new { job.Id, result });
        Assert.Equal("Launched", (await response).Code);
        await a.Post<Reply>("pair/share", new { id = bob.Id, target = bob.Id });
        Assert.Equal(bob.Id, (await Sync(b, "Beta", apps)).Presenter);
        using var discord = new HttpClient();
        using var scheduled = await discord.PostAsync(address + "/simulate-discord-alarm", null);
        scheduled.EnsureSuccessStatusCode();
        Assert.Empty((await Sync(a, "Alpha", [])).Alarms);
        var alarm = Assert.Single((await Sync(b, "Beta", apps)).Alarms);
        Assert.True(alarm.Due > DateTimeOffset.UtcNow);
        var ack = await b.Post<Snapshot>("sync", new { name = "Beta", apps, idleSeconds = 0, acks = new[] { alarm.Id } });
        Assert.Empty(ack.Alarms);
        await b.Post<Reply>("pair/remove", new { target = alice.Id });
        Assert.Null((await Sync(a, "Alpha", [])).Peer);
        var denied = await Assert.ThrowsAsync<BotError>(() => a.Post<Reply>("launch", new { target = bob.Id, app = "player" }));
        Assert.Equal("Denied", denied.Code);
    }
}

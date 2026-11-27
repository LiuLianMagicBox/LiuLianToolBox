using System.ComponentModel;
using System.Diagnostics;

namespace LiuLian.ToolBox;

public sealed record Guidance(Channel? Join, string? Share, bool NotifyJoin, bool NotifyShare);

public sealed class VoiceGuide
{
    private (string Peer, string Guild, string Room)? previousJoin;
    private (string Peer, string Guild, string Room, string Source)? previousShare;

    public Guidance Update(Snapshot? state, bool follow, ShareSource? source, bool sourceAvailable)
    {
        var peer = state?.Peer;
        var other = peer is { Online: true, SameAccount: false, Voice.Valid: true } ? peer.Voice : null;
        var together = other?.SamePlace(state?.Voice) == true;
        var join = follow && !together ? other : null;
        (string, string, string)? joinKey = join is null ? null : (peer!.Id, join.GuildId, join.ChannelId);
        var sharing = together && state!.Presenter == state.Id;
        var share = !sharing ? null : source is not null && sourceAvailable ? "GoLive" : "Reselect";
        (string, string, string, string)? shareKey = sharing ? (peer!.Id, other!.GuildId, other.ChannelId, share + source?.Id) : null;
        var result = new Guidance(join, share, joinKey.HasValue && joinKey != previousJoin,
            shareKey.HasValue && shareKey != previousShare);
        previousJoin = joinKey;
        previousShare = shareKey;
        return result;
    }
}

public sealed class DeviceActions(Settings settings, Action save, Action<string> openProgram)
{
    // Persist the reservation before starting a process. A lost reply or restart can then
    // report an uncertain outcome without starting a second copy.
    public string Receive(Snapshot state, Launch job, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(job.Id) || settings.DeviceId != state.Id || job.Target != state.Id ||
            state.Peer is null || job.From != state.Peer.Id) return "Denied";
        if (settings.Results.TryGetValue(job.Id, out var past)) return past.Code;
        var app = settings.Apps.Find(x => x.Id == job.App);
        if (app is null || !Path.IsPathFullyQualified(app.Path) || !File.Exists(app.Path))
            return "AppMissing";
        settings.Results[job.Id] = new(now, "Uncertain");
        try { save(); }
        catch { settings.Results.Remove(job.Id); throw; }
        string code;
        try { openProgram(app.Path); code = "Launched"; }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        { code = "LaunchFailed"; }
        settings.Results[job.Id] = new(now, code);
        save();
        return code;
    }

    public static void OpenProgram(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)!
        });
        if (process is null) throw new InvalidOperationException();
    }

    public string[] AcceptAlarms(Alarm[] incoming)
    {
        var added = incoming.DistinctBy(a => a.Id).Where(a => !settings.Alarms.ContainsKey(a.Id)).ToArray();
        foreach (var alarm in added) settings.Alarms.Add(alarm.Id, new(alarm, false));
        try { if (added.Length > 0) save(); }
        catch
        {
            foreach (var alarm in added) settings.Alarms.Remove(alarm.Id);
            throw;
        }
        return incoming.Select(alarm => alarm.Id).Distinct().ToArray();
    }

    public IReadOnlyList<Alarm> TakeDue(DateTimeOffset now)
    {
        var due = settings.Alarms.Values.Where(a => !a.Fired && a.Alarm.Due <= now)
            .Select(a => a.Alarm).ToArray();
        foreach (var alarm in due) settings.Alarms[alarm.Id] = new(alarm, true);
        // Keep receipts well beyond the lifetime of a relay request.
        var oldJobs = settings.Results.Where(x => x.Value.At < now.AddDays(-1)).Select(x => x.Key).ToArray();
        foreach (var key in oldJobs) settings.Results.Remove(key);
        try { if (due.Length + oldJobs.Length > 0) save(); }
        catch
        {
            foreach (var alarm in due) settings.Alarms[alarm.Id] = new(alarm, false);
            throw;
        }
        return due;
    }

    public void ForgetAcknowledgedAlarms(IEnumerable<string> stillOnServer)
    {
        var retained = stillOnServer.ToHashSet();
        var finished = settings.Alarms.Where(x => x.Value.Fired && !retained.Contains(x.Key)).Select(x => x.Key).ToArray();
        foreach (var id in finished) settings.Alarms.Remove(id);
        if (finished.Length > 0) save();
    }
}

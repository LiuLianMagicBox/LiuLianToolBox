using System.Text.Json;

namespace LiuLian.ToolBox;

public sealed record LocalApp(string Id, string Name, string Path);
public sealed record AppEntry(string Id, string Name);
public sealed record Device(string Id, string Name);
public sealed record Channel(string GuildId, string ChannelId)
{
    public string? GuildName { get; init; }
    public string? ChannelName { get; init; }
    public bool SamePlace(Channel? other) => other is not null && GuildId == other.GuildId && ChannelId == other.ChannelId;
    public bool Valid => IsSnowflake(GuildId) && IsSnowflake(ChannelId);
    private static bool IsSnowflake(string value) => ulong.TryParse(value,
        System.Globalization.NumberStyles.None, null, out var id) && id > 0 && value == id.ToString();
    public Uri Link => Valid ? new($"https://discord.com/channels/{GuildId}/{ChannelId}") :
        throw new InvalidOperationException();
}
public sealed record ShareSource(string Id, string Name);
public sealed record Peer(string Id, string Name, bool Online, bool SameAccount, Channel? Voice, AppEntry[] Apps);
public sealed record Alarm(string Id, DateTimeOffset Due);
public sealed record Launch(string Id, string From, string Target, string App);
public sealed record IdleSettings(bool Enabled, int Minutes, string Reply);
public sealed record MailSettings(bool Enabled, string Route);
public sealed record PrivateChannel(string GuildId, string ChannelId, string Name, int Limit);
public sealed record Snapshot(string Id, string Name, Channel? Voice, Peer? Peer, string Presenter,
    Device[] Devices, string DefaultDevice, IdleSettings? Idle, MailSettings? Mailbox,
    Alarm[] Alarms, Launch[] Launches);
public sealed record Credentials(string Id, string Token);
public sealed record LaunchResult(DateTimeOffset At, string Code);
public sealed record SavedAlarm(Alarm Alarm, bool Fired);

public sealed class Settings
{
    public string Appearance { get; set; } = "System";
    public string Language { get; set; } = "ZH_TW";
    public string Name { get; set; } = Environment.MachineName;
    public bool Follow { get; set; }
    public ShareSource? Source { get; set; }
    public List<LocalApp> Apps { get; set; } = [];
    public string Endpoint { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string ProtectedToken { get; set; } = "";
    public Dictionary<string, LaunchResult> Results { get; set; } = [];
    public Dictionary<string, SavedAlarm> Alarms { get; set; } = [];
}

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public Settings Load() => File.Exists(path)
        ? JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(path), Json) ?? throw new JsonException()
        : new();

    public void Save(Settings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, settings, Json);
                file.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }
}

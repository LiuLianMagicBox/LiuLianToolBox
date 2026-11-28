using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LiuLian.ToolBox;

public sealed class BotError(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record Reply(string Code);
public sealed record Invite(string Code);

public sealed class BotClient : IDisposable
{
    public const string ServiceUrl = "https://www.liulian.dev/toolbox";
    private readonly HttpClient http;

    internal static bool IsOfficialService(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.AbsoluteUri.TrimEnd('/') == ServiceUrl;

    public BotClient(string? token = null) : this(token, new HttpClientHandler()) { }

    internal BotClient(string? token, HttpClientHandler handler)
    {
        handler.AllowAutoRedirect = false;
        handler.UseCookies = false;
        http = new HttpClient(handler)
        {
            BaseAddress = new Uri(ServiceUrl + "/v1/"), Timeout = TimeSpan.FromSeconds(25),
            MaxResponseContentBufferSize = 1024 * 1024
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LiuLianToolBox",
            typeof(BotClient).Assembly.GetName().Version!.ToString(3)));
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<T> Post<T>(string route, object body, CancellationToken ct = default)
    {
        if (route is not ("link" or "unlink" or "sync" or "result" or "launch" or "default" or
            "pair/invite" or "pair/accept" or "pair/remove" or "pair/share" or "voice/join" or
            "channels" or "channel" or "idle" or "mailbox"))
            throw new ArgumentOutOfRangeException(nameof(route));
        using var response = await http.PostAsJsonAsync(route, body, ct);
        if ((int)response.StatusCode is >= 300 and < 400) throw new BotError("BotUnavailable");
        if (!response.IsSuccessStatusCode)
        {
            Reply? reason = null;
            try { reason = await response.Content.ReadFromJsonAsync<Reply>(ct); }
            catch (JsonException) { }
            throw new BotError(reason?.Code ?? "BotUnavailable");
        }
        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new BotError("BotUnavailable");
    }

    public void Dispose() => http.Dispose();
}

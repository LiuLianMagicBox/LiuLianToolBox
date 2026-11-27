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
    private readonly HttpClient http;
    public static Uri Address(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.UserInfo != "" ||
            uri.Query != "" || uri.Fragment != "" ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new BotError("InvalidUrl");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/v1/");
    }

    public BotClient(string address, string? token)
    {
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = Address(address), Timeout = TimeSpan.FromSeconds(25),
            MaxResponseContentBufferSize = 1024 * 1024
        };
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<T> Post<T>(string route, object body, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(route, body, ct);
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

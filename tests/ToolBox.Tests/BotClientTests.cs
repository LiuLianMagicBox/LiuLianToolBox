using System.Net;
using LiuLian.ToolBox;
using Xunit;

namespace ToolBox.Tests;

public class BotClientTests
{
    private sealed class RedirectHandler : HttpClientHandler
    {
        public List<(Uri Uri, string? Token)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("LiuLianToolBox", Assert.Single(request.Headers.UserAgent).Product!.Name);
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.Parameter));
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://another.example/v1/link");
            return Task.FromResult(response);
        }
    }

    [Theory]
    [InlineData("link", null)]
    [InlineData("sync", "device-token")]
    public async Task RequestsStayOnTheOfficialApi(string route, string? token)
    {
        var handler = new RedirectHandler();
        using var client = new BotClient(token, handler);
        var error = await Assert.ThrowsAsync<BotError>(() => client.Post<Reply>(route, new { }));
        Assert.Equal("BotUnavailable", error.Code);
        Assert.False(handler.AllowAutoRedirect);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://www.liulian.dev/toolbox/v1/" + route, request.Uri.AbsoluteUri);
        Assert.Equal(token, request.Token);
    }

    [Theory]
    [InlineData("https://another.example/v1/sync")]
    [InlineData("//another.example/v1/sync")]
    [InlineData("/v1/sync")]
    [InlineData("../link")]
    [InlineData("sync?next=other")]
    [InlineData("mail/received")]
    public async Task RejectsUnknownRoutesBeforeSendingCredentials(string route)
    {
        var handler = new RedirectHandler();
        using var client = new BotClient("device-token", handler);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Post<Reply>(route, new { }));
        Assert.Empty(handler.Requests);
    }
}

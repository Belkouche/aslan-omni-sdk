using System.Net;
using System.Text;
using Aslan.Omni.Auth;
using Aslan.Omni.Exceptions;
using Aslan.Omni.Models;

namespace Aslan.Omni.Tests;

/// <summary>
/// Pins the LAN routes and response shapes to what the terminal app serves
/// (aslan-omni OmniHttpServer.kt): POST /reversal, and /admin/pos createdAt in epoch seconds.
/// </summary>
public class OmniClientLanContractTests : IDisposable
{
    private const string TerminalId = "EMU-1";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"omni-lan-{Guid.NewGuid()}.json");

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private async Task<OmniClient> ClientAsync(FakeHandler handler)
    {
        using (var store = new OmniTokenStore(_path))
        {
            await store.SaveAsync(new SavedToken(TerminalId, "tok-1", new string('a', 64),
                "127.0.0.1", 8080, "TILL-01", DateTimeOffset.UtcNow));
        }
        return new OmniClient(new OmniClientOptions
        {
            TokenStorePath = _path,
            HttpClientFactory = () => new HttpClient(handler),
        });
    }

    [Fact]
    public async Task ReverseAsync_PostsToReversalRoute()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"status":"reversed","ref":"R-2","responseCode":"000"}""");
        using var client = await ClientAsync(handler);

        var result = await client.ReverseAsync(TerminalId, new ReversalRequest { Ref = "R-2", OriginalRef = "R-1", Stan = "000123" });

        Assert.Equal(HttpMethod.Post, handler.Last!.Method);
        Assert.Equal("/reversal", handler.Last.RequestUri!.AbsolutePath);
        Assert.Equal("reversed", result.Status);
    }

    [Fact]
    public async Task ListPosAsync_ParsesCreatedAtEpochSeconds()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            """{"pos":[{"tokenId":"tok-1","posId":"TILL-01","posLabel":"Caisse 1","createdAt":1762646400}]}""");
        using var client = await ClientAsync(handler);

        var list = await client.ListPosAsync(TerminalId);

        var pos = Assert.Single(list);
        Assert.Equal("TILL-01", pos.PosId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1762646400), pos.PairedAt);
    }

    [Fact]
    public async Task ListPosAsync_MapsHttpErrorToOmniException()
    {
        var handler = new FakeHandler(HttpStatusCode.Unauthorized, """{"error":"unknown token"}""");
        using var client = await ClientAsync(handler);

        var ex = await Assert.ThrowsAsync<OmniException>(() => client.ListPosAsync(TerminalId));
        Assert.Equal(OmniErrorCode.Unauthorized, ex.Code);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}

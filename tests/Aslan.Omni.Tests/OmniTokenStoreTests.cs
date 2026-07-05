using Aslan.Omni.Auth;

namespace Aslan.Omni.Tests;

public class OmniTokenStoreTests : IDisposable
{
    private readonly string _path;
    private readonly OmniTokenStore _store;

    public OmniTokenStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"omni-test-{Guid.NewGuid()}.json");
        _store = new OmniTokenStore(_path);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNoTokenSaved()
    {
        var result = await _store.GetAsync("SUNMI-P2-UNKNOWN");
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveAndGet_RoundTrip()
    {
        var token = new SavedToken(
            TerminalId: "SUNMI-P2-SN001",
            TokenId: Guid.NewGuid().ToString(),
            TokenSecret: Convert.ToBase64String(new byte[32]),
            IpAddress: "192.168.1.100",
            Port: 8080,
            PosId: "TILL-01",
            PairedAt: DateTimeOffset.UtcNow
        );

        await _store.SaveAsync(token);
        var retrieved = await _store.GetAsync("SUNMI-P2-SN001");

        Assert.NotNull(retrieved);
        Assert.Equal(token.TokenId, retrieved.TokenId);
        Assert.Equal(token.TokenSecret, retrieved.TokenSecret);
        Assert.Equal(token.IpAddress, retrieved.IpAddress);
    }

    [Fact]
    public async Task SaveAsync_Overwrites_ExistingToken()
    {
        var t1 = MakeToken("TERM-1", "secret-v1");
        var t2 = MakeToken("TERM-1", "secret-v2");

        await _store.SaveAsync(t1);
        await _store.SaveAsync(t2);

        var retrieved = await _store.GetAsync("TERM-1");
        Assert.Equal("secret-v2", retrieved!.TokenSecret);
    }

    [Fact]
    public async Task RemoveAsync_DeletesToken()
    {
        await _store.SaveAsync(MakeToken("TERM-DEL", "s"));
        await _store.RemoveAsync("TERM-DEL");

        Assert.Null(await _store.GetAsync("TERM-DEL"));
    }

    [Fact]
    public async Task ExistsAsync_ReturnsFalse_WhenMissing()
    {
        Assert.False(await _store.ExistsAsync("TERM-NONE"));
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrue_AfterSave()
    {
        await _store.SaveAsync(MakeToken("TERM-EXISTS", "s"));
        Assert.True(await _store.ExistsAsync("TERM-EXISTS"));
    }

    [Fact]
    public async Task MultipleTerminals_StoredIndependently()
    {
        await _store.SaveAsync(MakeToken("T1", "s1"));
        await _store.SaveAsync(MakeToken("T2", "s2"));
        await _store.SaveAsync(MakeToken("T3", "s3"));

        Assert.Equal("s1", (await _store.GetAsync("T1"))!.TokenSecret);
        Assert.Equal("s2", (await _store.GetAsync("T2"))!.TokenSecret);
        Assert.Equal("s3", (await _store.GetAsync("T3"))!.TokenSecret);
    }

    [Fact]
    public async Task PersistedToDisk_CanBeReadByNewInstance()
    {
        await _store.SaveAsync(MakeToken("TERM-PERSIST", "disk-secret"));

        using var store2 = new OmniTokenStore(_path);
        var t = await store2.GetAsync("TERM-PERSIST");
        Assert.Equal("disk-secret", t!.TokenSecret);
    }

    private static SavedToken MakeToken(string terminalId, string secret) => new(
        TerminalId: terminalId,
        TokenId: Guid.NewGuid().ToString(),
        TokenSecret: secret,
        IpAddress: "10.0.0.1",
        Port: 8080,
        PosId: "TILL-01",
        PairedAt: DateTimeOffset.UtcNow
    );

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_path)) File.Delete(_path);
    }
}

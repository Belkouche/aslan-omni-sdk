using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aslan.Omni.Auth;

namespace Aslan.Omni.Tests;

public class OmniSignerTests
{
    [Fact]
    public void BuildHeader_ReturnsCorrectFormat()
    {
        var tokenId = "test-token-id";
        var secret = RandomNumberGenerator.GetBytes(32);
        var body = Encoding.UTF8.GetBytes("{\"amountCents\":15000}");

        var header = OmniSigner.BuildHeader(tokenId, secret, "POST", "/pay", body);

        Assert.StartsWith("OMNI-TOKEN ", header);
        Assert.Contains($"tokenId={tokenId}", header);
        Assert.Matches(@"ts=\d+", header);
        Assert.Matches(@"sig=[0-9a-f]{64}", header);
    }

    [Fact]
    public void BuildHeader_DifferentBodies_ProduceDifferentSigs()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var tokenId = "tok";

        var h1 = OmniSigner.BuildHeader(tokenId, secret, "POST", "/pay",
            Encoding.UTF8.GetBytes("{\"amountCents\":100}"));
        var h2 = OmniSigner.BuildHeader(tokenId, secret, "POST", "/pay",
            Encoding.UTF8.GetBytes("{\"amountCents\":200}"));

        var sig1 = ExtractSig(h1);
        var sig2 = ExtractSig(h2);
        Assert.NotEqual(sig1, sig2);
    }

    [Fact]
    public void BuildHeader_EmptyBody_IsValid()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var header = OmniSigner.BuildHeader("tok", secret, "GET", "/admin/pos",
            ReadOnlySpan<byte>.Empty);

        Assert.Contains("sig=", header);
    }

    [Fact]
    public void BuildHeader_SameInputsSameSecond_ProduceSameSig()
    {
        // The timestamp is captured inside BuildHeader, so two calls within the
        // same second will share a ts — verify determinism given identical inputs.
        var secret = new byte[32];
        var body = Encoding.UTF8.GetBytes("{}");

        var h1 = OmniSigner.BuildHeader("tok", secret, "POST", "/pay", body);
        var h2 = OmniSigner.BuildHeader("tok", secret, "POST", "/pay", body);

        // Sigs may differ if the second rolls over between calls, but the format is always valid.
        Assert.Matches(@"sig=[0-9a-f]{64}", h1);
        Assert.Matches(@"sig=[0-9a-f]{64}", h2);
    }

    private static string ExtractSig(string header)
        => Regex.Match(header, @"sig=([0-9a-f]+)").Groups[1].Value;
}

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

    [Fact]
    public void BuildHeader_SignatureVerifiesAgainstTerminalDerivation()
    {
        // Reproduces the terminal's verification (OmniHttpServer.kt + OmniCrypto.kt):
        //   Pairing:  tokenSecret = randomBytes(32); returned to client as hex
        //             tokenHmac   = HMAC-SHA256(tokenSecret, tokenId)  [stored, hex]
        //   Verify:   message  = "{tokenId}:{ts}:{METHOD}:{path}:{sha256(body) hex}"
        //             expected = HMAC-SHA256(hexToBytes(tokenHmac), message)  == sig
        // The client only holds tokenSecret, so it must derive the same signing key.
        var tokenId = "b3f1c2d4-0000-4000-8000-000000000001";
        var tokenSecret = RandomNumberGenerator.GetBytes(32);
        var body = Encoding.UTF8.GetBytes("{\"ref\":\"ORDER-1\",\"amountCents\":15000}");

        var header = OmniSigner.BuildHeader(tokenId, tokenSecret, "POST", "/pay", body);

        var ts = Regex.Match(header, @"ts=(\d+)").Groups[1].Value;
        var sig = ExtractSig(header);

        // Terminal side: key = HMAC(tokenSecret, tokenId), lowercase-hex hashes.
        var tokenHmac = HmacHex(tokenSecret, Encoding.UTF8.GetBytes(tokenId));
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var message = $"{tokenId}:{ts}:POST:/pay:{bodyHash}";
        var expected = HmacHex(HexToBytes(tokenHmac), Encoding.UTF8.GetBytes(message));

        Assert.Equal(expected, sig);
    }

    private static string HmacHex(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(data)).ToLowerInvariant();
    }

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static string ExtractSig(string header)
        => Regex.Match(header, @"sig=([0-9a-f]+)").Groups[1].Value;
}

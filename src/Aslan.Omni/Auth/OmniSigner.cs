using System.Security.Cryptography;
using System.Text;
#if NET48 || NETFRAMEWORK
using Aslan.Omni.Polyfills;
#endif

namespace Aslan.Omni.Auth;

/// <summary>
/// Builds the OMNI-TOKEN Authorization header.
///
/// Format:  OMNI-TOKEN tokenId=&lt;uuid&gt;,ts=&lt;unix-seconds&gt;,sig=&lt;hex&gt;
/// Message: "{tokenId}:{ts}:{METHOD}:{path}:{body-sha256-hex}"
/// sig:     HMAC-SHA256(tokenSecret, message)
/// </summary>
internal static class OmniSigner
{
    internal static string BuildHeader(
        string tokenId,
        byte[] tokenSecret,
        string method,
        string path,
        ReadOnlySpan<byte> body)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

#if NET48 || NETFRAMEWORK
        var bodyHash = Net48Compat.Sha256Hash(body.ToArray());
        var bodyHashHex = Net48Compat.ToHexString(bodyHash);
#else
        Span<byte> bodyHash = stackalloc byte[32];
        SHA256.HashData(body, bodyHash);
        var bodyHashHex = Convert.ToHexString(bodyHash).ToLowerInvariant();
#endif

        var message = $"{tokenId}:{ts}:{method.ToUpperInvariant()}:{path}:{bodyHashHex}";
        var messageBytes = Encoding.UTF8.GetBytes(message);

        using var hmac = new HMACSHA256(tokenSecret);
        var sigBytes = hmac.ComputeHash(messageBytes);
#if NET48 || NETFRAMEWORK
        var sig = Net48Compat.ToHexString(sigBytes);
#else
        var sig = Convert.ToHexString(sigBytes).ToLowerInvariant();
#endif

        return $"OMNI-TOKEN tokenId={tokenId},ts={ts},sig={sig}";
    }
}

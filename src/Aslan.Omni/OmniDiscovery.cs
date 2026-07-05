using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aslan.Omni.Models;
#if NET48 || NETFRAMEWORK
using Aslan.Omni.Polyfills;
#endif

namespace Aslan.Omni;

/// <summary>
/// Discovers Aslan Omni terminals on the local network via UDP broadcast.
///
/// Protocol:
///   Send (broadcast):  "OMNI-DISC:{16-byte-random-hex-nonce}"
///   Receive (unicast): JSON { "device": "...", "model": "...", "version": "...", "hmac": "..." }
/// </summary>
public static class OmniDiscovery
{
    /// <param name="isPairedFunc">
    /// Optional delegate that returns true when the caller has a saved token for the given terminal ID.
    /// Used to populate <see cref="OmniTerminal.IsPaired"/>.
    /// </param>
    public static async Task<IReadOnlyList<OmniTerminal>> ScanAsync(
        int port = 8481,
        int httpPort = 8080,
        TimeSpan timeout = default,
        Func<string, Task<bool>>? isPairedFunc = null,
        CancellationToken ct = default)
    {
        if (timeout == default) timeout = TimeSpan.FromSeconds(3);

#if NET48 || NETFRAMEWORK
        var nonce = Net48Compat.ToHexString(RandomBytes(16));
#else
        var nonce = Convert.ToHexString(RandomBytes(16)).ToLowerInvariant();
#endif
        var message = Encoding.UTF8.GetBytes($"OMNI-DISC:{nonce}");
        var results = new List<OmniTerminal>();
        var seen = new HashSet<string>();

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        udp.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;

        await udp.SendAsync(message, message.Length, new IPEndPoint(IPAddress.Broadcast, port));

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var recv = await ReceiveWithTimeoutAsync(udp, deadline, ct);
                if (recv == null) break;

                var json = Encoding.UTF8.GetString(recv.Value.Buffer);
                var doc = JsonDocument.Parse(json).RootElement;

                var deviceId = doc.GetProperty("device").GetString() ?? "";
                if (!seen.Add(deviceId)) continue;

                var model = doc.GetProperty("model").GetString() ?? "";
                var version = doc.GetProperty("version").GetString() ?? "";
                var ip = recv.Value.RemoteEndPoint.Address.ToString();

                var isPaired = isPairedFunc != null && await isPairedFunc(deviceId);

                results.Add(new OmniTerminal
                {
                    Id = deviceId,
                    IpAddress = ip,
                    Port = httpPort,
                    Model = model,
                    Version = version,
                    IsPaired = isPaired,
                });
            }
            catch (SocketException) { break; }
            catch (JsonException) { /* malformed response — skip */ }
        }

        return results;
    }

    private static async Task<UdpReceiveResult?> ReceiveWithTimeoutAsync(
        UdpClient udp,
        DateTimeOffset deadline,
        CancellationToken ct)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(remaining);
        try
        {
#if NET48
            return await udp.ReceiveAsync();
#else
            return await udp.ReceiveAsync(cts.Token);
#endif
        }
        catch (OperationCanceledException) { return null; }
        catch (SocketException) { return null; }
    }

    private static byte[] RandomBytes(int count)
    {
#if NET48 || NETFRAMEWORK
        return Net48Compat.GetRandomBytes(count);
#else
        var buf = new byte[count];
        RandomNumberGenerator.Fill(buf);
        return buf;
#endif
    }
}

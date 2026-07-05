namespace Aslan.Omni.Models;

/// <summary>
/// Describes an Aslan Omni terminal discovered on the LAN.
/// </summary>
public sealed record OmniTerminal
{
    /// <summary>
    /// Stable device identifier, e.g. "SUNMI-P2-SN1234ABCD".
    /// Used as the key for token storage and subsequent API calls.
    /// </summary>
    public required string Id { get; init; }

    public required string IpAddress { get; init; }

    /// <summary>HTTP API port. Normally 8080.</summary>
    public int Port { get; init; } = 8080;

    public required string Model { get; init; }

    public required string Version { get; init; }

    /// <summary>True when a valid pairing token for this terminal is present in the local token store.</summary>
    public bool IsPaired { get; init; }

    public Uri BaseUri => new($"http://{IpAddress}:{Port}");
}

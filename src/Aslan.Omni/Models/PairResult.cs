namespace Aslan.Omni.Models;

public sealed record PairResult
{
    public required string TokenId { get; init; }

    /// <summary>The terminal's stable device ID (e.g. "SUNMI-P2-SN1234ABCD").</summary>
    public required string TerminalId { get; init; }

    public required string TerminalSerial { get; init; }

    public required string TerminalModel { get; init; }

    public required string PosId { get; init; }
}

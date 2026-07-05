namespace Aslan.Omni.Models;

public sealed record PosInfo
{
    public required string TokenId { get; init; }
    public required string PosId { get; init; }
    public required string PosLabel { get; init; }
    public required DateTimeOffset PairedAt { get; init; }
}

namespace Aslan.Omni.Models;

public sealed record ReversalRequest
{
    /// <summary>Reference for this reversal transaction.</summary>
    public required string Ref { get; init; }

    /// <summary>Reference of the original payment to reverse.</summary>
    public required string OriginalRef { get; init; }

    /// <summary>STAN of the original approved transaction.</summary>
    public required string Stan { get; init; }
}

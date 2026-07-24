namespace Aslan.Omni.Models;

public sealed record CloudPairResult
{
    public required string DeviceId { get; init; }
    public required string MerchantId { get; init; }
    public required string MerchantName { get; init; }
    public required string ApiKey { get; init; }
}

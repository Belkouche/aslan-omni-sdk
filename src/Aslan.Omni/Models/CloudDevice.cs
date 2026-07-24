namespace Aslan.Omni.Models;

public sealed record CloudDevice
{
    public required string DeviceId { get; init; }
    public required string MerchantId { get; init; }
    public required string MerchantName { get; init; }
    public bool Connected { get; init; }
    public bool Active { get; init; }
}

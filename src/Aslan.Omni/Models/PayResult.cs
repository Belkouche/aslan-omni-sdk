namespace Aslan.Omni.Models;

public sealed record PayResult
{
    /// <summary>"approved" | "declined" | "cancelled" | "timeout" | "reversed"</summary>
    public required string Status { get; init; }

    public required string Ref { get; init; }

    public bool IsApproved => Status == "approved";
    public bool IsDeclined => Status == "declined";
    public bool IsCancelled => Status == "cancelled";
    public bool IsTimeout => Status == "timeout";
    public bool IsReversed => Status == "reversed";

    /// <summary>Cloud command identifier. Populated only for cloud-mode payments.</summary>
    public string? CommandId { get; init; }

    /// <summary>ISO 8583 response code. "000" on approval.</summary>
    public string? ResponseCode { get; init; }

    public string? ResponseMessage { get; init; }

    public string? AuthNumber { get; init; }

    /// <summary>System Trace Audit Number.</summary>
    public string? Stan { get; init; }

    /// <summary>Retrieval Reference Number.</summary>
    public string? Rrn { get; init; }

    /// <summary>Masked PAN, e.g. "476173******4521".</summary>
    public string? CardNumber { get; init; }

    public string? CardScheme { get; init; }

    public string? TerminalId { get; init; }

    public string? MerchantId { get; init; }

    /// <summary>Format: ddMMyyy</summary>
    public string? TransactionDate { get; init; }

    /// <summary>Format: HHmmss</summary>
    public string? TransactionTime { get; init; }

    public int? AmountCents { get; init; }
}

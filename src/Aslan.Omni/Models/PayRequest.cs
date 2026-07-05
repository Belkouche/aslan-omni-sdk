namespace Aslan.Omni.Models;

public sealed record PayRequest
{
    /// <summary>
    /// Idempotency key. Max 40 characters. If a request with the same Ref is already
    /// in-progress on the terminal, the call returns 409. If completed within 24 hours,
    /// the cached result is returned.
    /// </summary>
    public required string Ref { get; init; }

    /// <summary>Amount in Moroccan centimes (e.g. 15000 = 150,00 DH).</summary>
    public required int AmountCents { get; init; }

    /// <summary>Cashier identifier — printed on receipt when provided.</summary>
    public string? CashierId { get; init; }

    /// <summary>Till / register identifier — printed on receipt when provided.</summary>
    public string? TillId { get; init; }

    /// <summary>Free-text description (e.g. "Table 7") — printed on receipt when provided.</summary>
    public string? Description { get; init; }
}

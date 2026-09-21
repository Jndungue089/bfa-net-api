namespace BfaNet.Domain.Entities;

public sealed class LedgerTransaction
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    /// <summary>Human-visible unique reference, e.g. BFA-20260921-7F3K9Q2M.</summary>
    public required string Reference { get; init; }
    public Guid InitiatedBy { get; init; }
    public Guid IdempotencyKey { get; init; }
    /// <summary>SHA-256 of the canonical request; a replay with the same key but a different body is rejected.</summary>
    public required string RequestHash { get; init; }
    public TransactionKind Kind { get; init; }
    public TransactionStatus Status { get; set; } = TransactionStatus.Completed;
    public Currency Currency { get; init; }
    public decimal Amount { get; init; }
    public decimal Fee { get; init; }
    public string? Description { get; init; }
    public string? OriginatorName { get; init; }
    public string? CounterpartyName { get; init; }
    public string? CounterpartyIban { get; init; }
    /// <summary>Service entity code / operator / phone — depends on <see cref="Kind"/>.</summary>
    public string? ExternalReference { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public List<LedgerEntry> Entries { get; init; } = [];
}

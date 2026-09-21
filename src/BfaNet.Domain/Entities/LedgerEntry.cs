namespace BfaNet.Domain.Entities;

public sealed class LedgerEntry
{
    public long Id { get; init; }
    public Guid TransactionId { get; init; }
    public Guid AccountId { get; init; }
    public LedgerDirection Direction { get; init; }
    public decimal Amount { get; init; }
    public decimal BalanceAfter { get; set; }
    public DateTimeOffset CreatedAt { get; init; }

    public LedgerTransaction? Transaction { get; init; }
}

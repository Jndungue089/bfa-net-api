namespace BfaNet.Domain.Entities;

public sealed class Account
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    /// <summary>Null for internal (settlement / fee) accounts.</summary>
    public Guid? CustomerId { get; init; }
    public required string Iban { get; init; }
    public required string AccountNumber { get; init; }
    public AccountType Type { get; init; }
    public Currency Currency { get; init; } = Currency.AOA;
    /// <summary>Only ever mutated through atomic SQL in the ledger; never assigned from application code.</summary>
    public decimal Balance { get; init; }
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    public string? Nickname { get; set; }
    public DateTimeOffset OpenedAt { get; init; }

    public Customer? Customer { get; init; }
}

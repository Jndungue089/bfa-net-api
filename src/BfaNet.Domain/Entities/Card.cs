namespace BfaNet.Domain.Entities;

/// <summary>Only the masked PAN tail is ever stored — never the full PAN, CVV or PIN.</summary>
public sealed class Card
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid CustomerId { get; init; }
    public Guid AccountId { get; init; }
    public CardProduct Product { get; init; }
    public required string ProductName { get; init; }
    public required string Last4 { get; init; }
    public required string HolderName { get; init; }
    public int ExpiryMonth { get; init; }
    public int ExpiryYear { get; init; }
    public CardStatus Status { get; set; } = CardStatus.Active;
    public bool OnlinePurchases { get; set; } = true;
    public bool Contactless { get; set; } = true;
    public bool AtmWithdrawals { get; set; } = true;
    /// <summary>Off by default: cards must be explicitly enabled for use outside Angola.</summary>
    public bool InternationalPayments { get; set; }
    public decimal DailyLimit { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}

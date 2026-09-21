namespace BfaNet.Domain.Entities;

public sealed class ExchangeRate
{
    public Currency Currency { get; init; }
    public decimal Buy { get; set; }
    public decimal Sell { get; set; }
    public required string Source { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

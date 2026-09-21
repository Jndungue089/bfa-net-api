namespace BfaNet.Domain.Entities;

public sealed class Beneficiary
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid CustomerId { get; init; }
    public required string Name { get; set; }
    public required string Iban { get; init; }
    public string? BankName { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}

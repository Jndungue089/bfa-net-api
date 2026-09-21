namespace BfaNet.Domain.Entities;

/// <summary>Append-only security/business audit trail. Never contains secrets or raw PII.</summary>
public sealed class AuditLog
{
    public long Id { get; init; }
    public Guid? CustomerId { get; init; }
    public required string Action { get; init; }
    public bool Success { get; init; }
    public string? Detail { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

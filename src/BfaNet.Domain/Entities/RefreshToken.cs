namespace BfaNet.Domain.Entities;

public sealed class RefreshToken
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid CustomerId { get; init; }
    /// <summary>All tokens descending from one login share a family; reuse of a rotated token revokes the family.</summary>
    public Guid FamilyId { get; init; }
    /// <summary>SHA-256 of the opaque token. The raw token is never persisted.</summary>
    public required string TokenHash { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Copied on rotation so a session has an absolute lifetime regardless of activity.</summary>
    public DateTimeOffset SessionStartedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
    public string? DeviceLabel { get; init; }
    public string? IpAddress { get; init; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

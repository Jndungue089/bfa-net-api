namespace BfaNet.Domain.Entities;

/// <summary>
/// Bank customer. PII fields (<see cref="Email"/>, <see cref="Phone"/>, <see cref="NationalId"/>,
/// <see cref="TaxId"/>, <see cref="Address"/>) are encrypted at rest by the persistence layer;
/// the *Hash fields are HMAC blind indexes used for exact-match lookups and uniqueness.
/// </summary>
public sealed class Customer
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    /// <summary>"Número de adesão" — the public login identifier (8 digits).</summary>
    public required string CustomerNumber { get; init; }
    public required string FullName { get; set; }

    public required string Email { get; set; }
    public required string EmailHash { get; set; }
    public required string Phone { get; set; }
    public required string PhoneHash { get; set; }
    public required string NationalId { get; set; }
    public required string NationalIdHash { get; set; }
    public string? TaxId { get; set; }
    public string? Address { get; set; }
    public DateOnly BirthDate { get; set; }

    public required string PasswordHash { get; set; }
    public required string PinHash { get; set; }
    public DateTimeOffset PasswordChangedAt { get; set; }

    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutEndsAt { get; set; }
    public int FailedPinCount { get; set; }
    public DateTimeOffset? PinLockoutEndsAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public CustomerStatus Status { get; set; } = CustomerStatus.Active;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Account> Accounts { get; } = [];

    public bool IsLockedOut(DateTimeOffset now) => LockoutEndsAt is { } end && end > now;
    public bool IsPinLockedOut(DateTimeOffset now) => PinLockoutEndsAt is { } end && end > now;
}

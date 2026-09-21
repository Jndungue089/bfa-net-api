namespace BfaNet.Domain.Entities;

/// <summary>
/// Revocable, per-device secret behind biometric login. The raw token lives only in the device's
/// biometric-protected Keychain/Keystore item; the server keeps its SHA-256.
/// </summary>
public sealed class DeviceCredential
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid CustomerId { get; init; }
    public required string TokenHash { get; init; }
    public string? DeviceLabel { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

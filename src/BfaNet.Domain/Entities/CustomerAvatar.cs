namespace BfaNet.Domain.Entities;

/// <summary>Profile picture. Kept in its own table so customer queries never drag image bytes along.</summary>
public sealed class CustomerAvatar
{
    public Guid CustomerId { get; init; }
    /// <summary>Derived server-side from the file's magic bytes, never from the client-supplied header.</summary>
    public required string ContentType { get; set; }
    public required byte[] Data { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

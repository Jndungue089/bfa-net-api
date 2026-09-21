using BfaNet.Application.Abstractions;
using BfaNet.Application.Common;
using BfaNet.Domain.Entities;
using BfaNet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BfaNet.Infrastructure.Services;

public sealed class AvatarService(BankDbContext db, IRequestContext ctx, IAuditWriter audit, TimeProvider clock) : IAvatarService
{
    /// <summary>Content type from magic bytes — the client's Content-Type header is never trusted.</summary>
    internal static string? Sniff(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        if (d.Length >= 8 && d[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (d.Length >= 12 && d[..4].SequenceEqual("RIFF"u8) && d.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    public async Task SaveAsync(byte[] original, CancellationToken ct)
    {
        var id = ctx.RequireCustomerId();
        if (original.Length == 0) throw AppException.Invalid("file", "Ficheiro vazio.");
        if (original.Length > IAvatarService.MaxUploadBytes) throw new AppException(ErrorCodes.Validation, "A imagem excede 10 MB.", 413);
        if (Sniff(original) is null) throw AppException.Invalid("file", "Formato não suportado (use JPEG, PNG ou WebP).");

        // Decoding/encoding is CPU-bound; keep it off the request's synchronous path.
        var data = await Task.Run(() => AvatarImageProcessor.ToAvatarJpeg(original, IAvatarService.MaxStoredBytes), ct);

        var now = clock.GetUtcNow();
        var existing = await db.CustomerAvatars.FirstOrDefaultAsync(a => a.CustomerId == id, ct);
        if (existing is null) db.CustomerAvatars.Add(new CustomerAvatar { CustomerId = id, ContentType = "image/jpeg", Data = data, UpdatedAt = now });
        else { existing.ContentType = "image/jpeg"; existing.Data = data; existing.UpdatedAt = now; }
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("profile.avatar_update", true, id, $"in={original.Length} out={data.Length}");
    }

    public async Task<AvatarFile?> GetAsync(CancellationToken ct)
    {
        var id = ctx.RequireCustomerId();
        return await db.CustomerAvatars.AsNoTracking().Where(a => a.CustomerId == id)
            .Select(a => new AvatarFile(a.Data, a.ContentType, a.UpdatedAt)).FirstOrDefaultAsync(ct);
    }

    public async Task DeleteAsync(CancellationToken ct)
    {
        var id = ctx.RequireCustomerId();
        await db.CustomerAvatars.Where(a => a.CustomerId == id).ExecuteDeleteAsync(ct);
        await audit.RecordAsync("profile.avatar_delete", true, id);
    }
}

using BfaNet.Application.Abstractions;
using BfaNet.Domain.Entities;
using BfaNet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BfaNet.Infrastructure.Services;

/// <summary>Writes through its own short-lived context so audit rows survive a rolled-back business transaction.</summary>
public sealed class AuditWriter(
    IDbContextFactory<BankDbContext> factory, IRequestContext ctx, TimeProvider clock, ILogger<AuditWriter> log) : IAuditWriter
{
    public async Task RecordAsync(string action, bool success, Guid? customerId = null, string? detail = null)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync();
            db.AuditLogs.Add(new AuditLog
            {
                CustomerId = customerId ?? ctx.CustomerId,
                Action = action,
                Success = success,
                Detail = detail is { Length: > 512 } ? detail[..512] : detail,
                IpAddress = ctx.IpAddress,
                UserAgent = ctx.UserAgent is { Length: > 256 } ua ? ua[..256] : ctx.UserAgent,
                CreatedAt = clock.GetUtcNow()
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Never let auditing break the request, but never lose the fact silently either.
            log.LogError(ex, "Falha a gravar auditoria {Action}", action);
        }
    }
}

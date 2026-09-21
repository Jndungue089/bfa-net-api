using BfaNet.Application.Abstractions;
using BfaNet.Application.Common;
using BfaNet.Domain;
using BfaNet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BfaNet.Infrastructure.Services;

/// <summary>Operations-PIN check used by every step-up authorised action (transfers, credit, instalments). Counts failures atomically.</summary>
public sealed class PinAuthorizer(BankDbContext db, ISecretHasher hasher, CredentialAttempts attempts, IAuditWriter audit, TimeProvider clock)
{
    public async Task VerifyAsync(Guid customerId, string pin, string auditAction, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var c = await db.Customers.AsNoTracking().Where(x => x.Id == customerId)
            .Select(x => new { x.PinHash, x.PinLockoutEndsAt, x.Status }).FirstOrDefaultAsync(ct)
            ?? throw new AppException(ErrorCodes.Unauthorized, "Sessão inválida.", 401);

        if (c.Status != CustomerStatus.Active) throw new AppException(ErrorCodes.Forbidden, "Conta indisponível.", 403);
        if (c.PinLockoutEndsAt > now) throw new AppException(ErrorCodes.PinLocked, "PIN bloqueado temporariamente. Tente mais tarde.", 423);

        if (!hasher.Verify(pin, c.PinHash))
        {
            await attempts.RegisterPinFailureAsync(customerId, ct);
            await audit.RecordAsync(auditAction, false, customerId, "bad-pin");
            throw new AppException(ErrorCodes.InvalidPin, "PIN incorrecto.", 422);
        }
        await attempts.ResetPinAsync(customerId, ct);
    }
}

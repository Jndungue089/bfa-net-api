using BfaNet.Application;
using BfaNet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BfaNet.Infrastructure.Services;

/// <summary>
/// Brute-force counters updated with atomic SQL (col = col + 1) so parallel guesses cannot
/// undercount by racing on a tracked entity.
/// </summary>
public sealed class CredentialAttempts(BankDbContext db, IOptions<SecurityOptions> options, TimeProvider clock)
{
    private readonly SecurityOptions _o = options.Value;

    public async Task RegisterLoginFailureAsync(Guid id, CancellationToken ct)
    {
        await db.Customers.Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.FailedLoginCount, c => c.FailedLoginCount + 1), ct);
        var count = await db.Customers.Where(c => c.Id == id).Select(c => c.FailedLoginCount).SingleAsync(ct);
        if (count >= _o.MaxLoginAttempts)
        {
            var until = clock.GetUtcNow().AddMinutes(_o.LockoutMinutes);
            await db.Customers.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.FailedLoginCount, 0).SetProperty(c => c.LockoutEndsAt, until), ct);
        }
    }

    public Task ResetLoginAsync(Guid id, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.Customers.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.FailedLoginCount, 0).SetProperty(c => c.LockoutEndsAt, (DateTimeOffset?)null)
            .SetProperty(c => c.LastLoginAt, now), ct);
    }

    public async Task RegisterPinFailureAsync(Guid id, CancellationToken ct)
    {
        await db.Customers.Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.FailedPinCount, c => c.FailedPinCount + 1), ct);
        var count = await db.Customers.Where(c => c.Id == id).Select(c => c.FailedPinCount).SingleAsync(ct);
        if (count >= _o.MaxPinAttempts)
        {
            var until = clock.GetUtcNow().AddMinutes(_o.PinLockoutMinutes);
            await db.Customers.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.FailedPinCount, 0).SetProperty(c => c.PinLockoutEndsAt, until), ct);
        }
    }

    public Task ResetPinAsync(Guid id, CancellationToken ct) =>
        db.Customers.Where(c => c.Id == id && (c.FailedPinCount != 0 || c.PinLockoutEndsAt != null))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.FailedPinCount, 0).SetProperty(c => c.PinLockoutEndsAt, (DateTimeOffset?)null), ct);
}

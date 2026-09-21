using BfaNet.Application.Abstractions;
using BfaNet.Application.Common;
using BfaNet.Application.Contracts;
using BfaNet.Domain;
using BfaNet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BfaNet.Infrastructure.Services;

public sealed class AccountService(BankDbContext db, IRequestContext ctx) : IAccountService
{
    private static readonly TimeSpan Wat = TimeSpan.FromHours(1);
    private const int DefaultPage = 20;

    public async Task<IReadOnlyList<AccountDto>> ListAsync(CancellationToken ct)
    {
        var id = ctx.RequireCustomerId();
        var rows = await db.Accounts.AsNoTracking().Where(a => a.CustomerId == id && a.Status != AccountStatus.Closed)
            .OrderBy(a => a.OpenedAt).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<AccountDto> GetAsync(Guid id, CancellationToken ct) => ToDto(await OwnedAsync(id, ct));

    public async Task<AccountDto> RenameAsync(Guid id, RenameAccountRequest request, CancellationToken ct)
    {
        var customerId = ctx.RequireCustomerId();
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.CustomerId == customerId, ct)
            ?? throw AppException.NotFound("Conta", feminine: true);
        account.Nickname = TextSanitizer.CleanOrNull(request.Nickname);
        await db.SaveChangesAsync(ct);
        return ToDto(account);
    }

    public async Task<StatementPage> StatementAsync(Guid id, StatementQuery q, CancellationToken ct)
    {
        await OwnedAsync(id, ct); // authorisation: the account must belong to the caller
        var limit = q.Limit ?? DefaultPage;

        var entries = db.LedgerEntries.AsNoTracking().Where(e => e.AccountId == id);
        if (q.From is { } from) { var f = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), Wat).ToUniversalTime(); entries = entries.Where(e => e.CreatedAt >= f); }
        if (q.To is { } to) { var t = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), Wat).ToUniversalTime(); entries = entries.Where(e => e.CreatedAt < t); }
        if (q.Direction is { } dir) entries = entries.Where(e => e.Direction == dir);
        if (q.Cursor is { } cursor) entries = entries.Where(e => e.Id < cursor);

        var rows = await entries.OrderByDescending(e => e.Id).Take(limit + 1)
            .Select(e => new StatementItem(
                e.Id, e.TransactionId, e.Transaction!.Reference, e.Transaction.Kind, e.Direction, e.Amount, e.BalanceAfter,
                e.Transaction.Description,
                e.Direction == LedgerDirection.Debit ? e.Transaction.CounterpartyName : e.Transaction.OriginatorName,
                e.CreatedAt))
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;
        var items = hasMore ? rows[..limit] : rows;
        return new StatementPage(items, hasMore ? items[^1].EntryId : null);
    }

    private async Task<Domain.Entities.Account> OwnedAsync(Guid id, CancellationToken ct)
    {
        var customerId = ctx.RequireCustomerId();
        // Same 404 for "missing" and "someone else's": no object-existence oracle (IDOR).
        return await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.CustomerId == customerId, ct)
            ?? throw AppException.NotFound("Conta", feminine: true);
    }

    private static AccountDto ToDto(Domain.Entities.Account a) =>
        new(a.Id, a.Iban, a.AccountNumber, a.Type, a.Currency, a.Balance, a.Status, a.Nickname, a.OpenedAt);
}

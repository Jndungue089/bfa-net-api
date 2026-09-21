using System.Security.Cryptography;
using BfaNet.Application.Common;
using BfaNet.Domain;
using BfaNet.Domain.Entities;
using BfaNet.Domain.Ledger;
using BfaNet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace BfaNet.Infrastructure.Services;

/// <summary>
/// Applies a balanced <see cref="LedgerPosting"/>. Balances change only through single-statement
/// atomic UPDATEs (`balance = balance ± x`), guarded in SQL against overdraft; legs are applied in
/// account-id order so concurrent postings cannot deadlock. Must run inside an open EF transaction.
/// </summary>
public sealed class LedgerPoster(BankDbContext db, TimeProvider clock)
{
    private const string RefAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public sealed record NewTransaction(
        Guid InitiatedBy, Guid IdempotencyKey, string RequestHash, TransactionKind Kind, decimal Amount, decimal Fee,
        string? Description, string? OriginatorName, string? CounterpartyName, string? CounterpartyIban, string? ExternalReference);

    public async Task<LedgerTransaction> PostAsync(NewTransaction spec, LedgerPosting posting, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Lançamento fora de transacção.");

        var ids = posting.Legs.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);
        if (accounts.Count != ids.Count) throw AppException.NotFound("Conta", feminine: true);

        var currency = accounts[posting.Legs[0].AccountId].Currency;
        if (accounts.Values.Any(a => a.Currency != currency))
            throw new AppException(ErrorCodes.Validation, "Operações entre moedas diferentes não são suportadas.", 422);

        var now = clock.GetUtcNow();
        var tx = new LedgerTransaction
        {
            Reference = NewReference(now), InitiatedBy = spec.InitiatedBy, IdempotencyKey = spec.IdempotencyKey,
            RequestHash = spec.RequestHash, Kind = spec.Kind, Currency = currency, Amount = spec.Amount, Fee = spec.Fee,
            Description = spec.Description, OriginatorName = spec.OriginatorName, CounterpartyName = spec.CounterpartyName,
            CounterpartyIban = spec.CounterpartyIban, ExternalReference = spec.ExternalReference, CreatedAt = now
        };

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var npgTx = (NpgsqlTransaction)db.Database.CurrentTransaction.GetDbTransaction();

        foreach (var leg in posting.Legs) // already ordered by account id
        {
            var account = accounts[leg.AccountId];
            var balanceAfter = leg.Direction == LedgerDirection.Debit
                ? await DebitAsync(conn, npgTx, account, leg.Amount, ct)
                : await CreditAsync(conn, npgTx, account, leg.Amount, ct);

            tx.Entries.Add(new LedgerEntry
            {
                AccountId = leg.AccountId, Direction = leg.Direction, Amount = leg.Amount,
                BalanceAfter = balanceAfter, CreatedAt = now
            });
        }

        db.Transactions.Add(tx);
        await db.SaveChangesAsync(ct);
        return tx;
    }

    private static async Task<decimal> DebitAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Account a, decimal amount, CancellationToken ct)
    {
        if (a.Status != AccountStatus.Active)
            throw new AppException(ErrorCodes.Conflict, "A conta de origem não está activa.", 409);

        // Internal GL accounts may go negative; customer accounts are guarded by the WHERE clause (and a CHECK constraint).
        var sql = a.Type == AccountType.Interna
            ? "UPDATE accounts SET balance = balance - @amt WHERE id = @id RETURNING balance"
            : "UPDATE accounts SET balance = balance - @amt WHERE id = @id AND status = 'Active' AND balance >= @amt RETURNING balance";
        var result = await ExecuteScalarAsync(conn, tx, sql, a.Id, amount, ct);
        return result ?? throw new AppException(ErrorCodes.InsufficientFunds, "Saldo insuficiente.", 422);
    }

    private static async Task<decimal> CreditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Account a, decimal amount, CancellationToken ct)
    {
        var result = await ExecuteScalarAsync(conn, tx,
            "UPDATE accounts SET balance = balance + @amt WHERE id = @id AND status <> 'Closed' RETURNING balance", a.Id, amount, ct);
        return result ?? throw new AppException(ErrorCodes.Conflict, "A conta de destino não pode receber fundos.", 409);
    }

    private static async Task<decimal?> ExecuteScalarAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, Guid id, decimal amt, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("amt", NpgsqlTypes.NpgsqlDbType.Numeric, amt);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is decimal d ? d : null;
    }

    private static string NewReference(DateTimeOffset now)
    {
        Span<char> suffix = stackalloc char[8];
        for (var i = 0; i < suffix.Length; i++) suffix[i] = RefAlphabet[RandomNumberGenerator.GetInt32(RefAlphabet.Length)];
        return $"BFA-{now:yyyyMMdd}-{suffix}";
    }
}

using System.Security.Cryptography;
using System.Text;
using BfaNet.Application;
using BfaNet.Application.Abstractions;
using BfaNet.Application.Common;
using BfaNet.Application.Contracts;
using BfaNet.Domain;
using BfaNet.Domain.Entities;
using BfaNet.Domain.Ledger;
using BfaNet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BfaNet.Infrastructure.Services;

public sealed class TransferService(
    BankDbContext db, IRequestContext ctx, ISecretHasher hasher, CredentialAttempts attempts, IAuditWriter audit,
    LedgerPoster poster, IBlindIndex blind, IOptions<BankingOptions> bankOpts, TimeProvider clock) : ITransferService
{
    private static readonly TimeSpan Wat = TimeSpan.FromHours(1); // Africa/Luanda, no DST
    private readonly BankingOptions _bank = bankOpts.Value;

    /// <summary>Everything needed to post a customer-initiated debit, independent of the product.</summary>
    private sealed record Move(
        Guid FromAccountId, string Pin, TransactionKind Kind, decimal Amount, decimal Fee, Guid CreditAccountId,
        string? Description, string? CounterpartyName, string? CounterpartyIban, string? ExternalReference, string AuditAction);

    // ---------- Public operations ----------

    public async Task<ResolveIbanResponse> ResolveIbanAsync(ResolveIbanRequest request, CancellationToken ct)
    {
        ctx.RequireCustomerId();
        var iban = TextSanitizer.NormalizeIban(request.Iban);
        if (!Iban.IsValid(iban)) return new ResolveIbanResponse(false, false, null, null);
        if (!Iban.IsBfa(iban)) return new ResolveIbanResponse(true, false, null, "Outro banco");

        var holder = await db.Accounts.AsNoTracking()
            .Where(a => a.Iban == iban && a.CustomerId != null && a.Status == AccountStatus.Active)
            .Select(a => a.Customer!.FullName).FirstOrDefaultAsync(ct);
        return holder is null
            ? new ResolveIbanResponse(false, false, null, "BFA")
            : new ResolveIbanResponse(true, true, TextSanitizer.MaskName(holder), "BFA");
    }

    public async Task<TransactionReceipt> TransferAsync(Guid key, TransferRequest r, CancellationToken ct)
    {
        var iban = TextSanitizer.NormalizeIban(r.ToIban);
        var description = TextSanitizer.CleanOrNull(r.Description);

        Guid creditAccount; decimal fee = 0; string? counterpartyName;
        if (Iban.IsBfa(iban))
        {
            var target = await db.Accounts.AsNoTracking()
                .Where(a => a.Iban == iban && a.CustomerId != null)
                .Select(a => new { a.Id, a.Status, Name = a.Customer!.FullName }).FirstOrDefaultAsync(ct);
            if (target is null || target.Status != AccountStatus.Active)
                throw AppException.Invalid(nameof(r.ToIban), "Conta de destino não encontrada.");
            if (target.Id == r.FromAccountId)
                throw AppException.Invalid(nameof(r.ToIban), "A conta de destino não pode ser a de origem.");
            creditAccount = target.Id;
            counterpartyName = target.Name;
        }
        else
        {
            // Interbank: a beneficiary name is mandatory, a flat fee applies.
            if (TextSanitizer.CleanOrNull(r.BeneficiaryName) is not { } name || !Patterns.PersonName.IsMatch(name))
                throw AppException.Invalid(nameof(r.BeneficiaryName), "Indique o nome do beneficiário.");
            creditAccount = SystemAccounts.InterbankSettlement;
            fee = _bank.InterbankFee;
            counterpartyName = name;
        }

        return await ExecuteAsync(key, new Move(r.FromAccountId, r.Pin, TransactionKind.Transfer, r.Amount, fee, creditAccount,
            description, counterpartyName, iban, null, "transfer.create"), ct);
    }

    public Task<TransactionReceipt> PayServiceAsync(Guid key, ServicePaymentRequest r, CancellationToken ct) =>
        ExecuteAsync(key, new Move(r.FromAccountId, r.Pin, TransactionKind.ServicePayment, r.Amount, 0, SystemAccounts.ServiceSettlement,
            $"Pagamento de serviços {r.EntityCode}", $"Entidade {r.EntityCode}", null, $"{r.EntityCode}/{r.Reference}", "payment.service"), ct);

    private static readonly Dictionary<RechargeProvider, string> ProviderName = new()
    {
        [RechargeProvider.Unitel] = "Unitel", [RechargeProvider.Africell] = "Africell", [RechargeProvider.Dstv] = "DStv",
        [RechargeProvider.Zap] = "ZAP", [RechargeProvider.Ende] = "ENDE",
    };

    public Task<TransactionReceipt> RechargeAsync(Guid key, RechargeRequest r, CancellationToken ct)
    {
        var mobile = r.Provider is RechargeProvider.Unitel or RechargeProvider.Africell;
        var identifier = mobile ? TextSanitizer.NormalizePhone(r.Identifier) : TextSanitizer.DigitsOnly(r.Identifier);
        var name = ProviderName[r.Provider];
        var description = r.Provider switch
        {
            RechargeProvider.Unitel or RechargeProvider.Africell => $"Carregamento {name} {identifier}",
            RechargeProvider.Ende => $"Pagamento ENDE · contador {identifier}",
            _ => $"Pagamento {name} · {identifier}",
        };
        return ExecuteAsync(key, new Move(r.FromAccountId, r.Pin, TransactionKind.TopUp, r.Amount, 0, SystemAccounts.TopUpSettlement,
            description, name, null, $"{r.Provider}:{identifier}", "payment.recharge"), ct);
    }

    public Task<TransactionReceipt> PayStateAsync(Guid key, StatePaymentRequest r, CancellationToken ct)
    {
        var reference = TextSanitizer.DigitsOnly(r.Reference);
        return ExecuteAsync(key, new Move(r.FromAccountId, r.Pin, TransactionKind.StatePayment, r.Amount, 0, SystemAccounts.StateSettlement,
            $"Pagamento ao Estado · {reference}", "Estado", null, reference, "payment.state"), ct);
    }

    // ---------- KWiK: instant transfer to a mobile number ----------

    private sealed record KwikTarget(Guid CustomerId, Guid AccountId, string Iban, string FullName);

    private async Task<KwikTarget?> FindKwikTargetAsync(string key, CancellationToken ct)
    {
        var phoneHash = blind.Compute("phone", TextSanitizer.NormalizePhone(key));
        var customer = await db.Customers.AsNoTracking()
            .Where(c => c.PhoneHash == phoneHash && c.Status == CustomerStatus.Active)
            .Select(c => new { c.Id, c.FullName }).FirstOrDefaultAsync(ct);
        if (customer is null) return null;

        var account = await db.Accounts.AsNoTracking()
            .Where(a => a.CustomerId == customer.Id && a.Status == AccountStatus.Active && a.Type != AccountType.Poupanca)
            .OrderBy(a => a.OpenedAt).Select(a => new { a.Id, a.Iban }).FirstOrDefaultAsync(ct);
        return account is null ? null : new KwikTarget(customer.Id, account.Id, account.Iban, customer.FullName);
    }

    public async Task<KwikResolveResponse> ResolveKwikAsync(KwikResolveRequest request, CancellationToken ct)
    {
        ctx.RequireCustomerId();
        var t = await FindKwikTargetAsync(request.Key, ct);
        return t is null ? new KwikResolveResponse(false, null) : new KwikResolveResponse(true, TextSanitizer.MaskName(t.FullName));
    }

    public async Task<TransactionReceipt> KwikTransferAsync(Guid key, KwikTransferRequest r, CancellationToken ct)
    {
        var me = ctx.RequireCustomerId();
        var target = await FindKwikTargetAsync(r.Key, ct)
            ?? throw AppException.Invalid(nameof(r.Key), "Chave KWiK não encontrada.");
        if (target.CustomerId == me) throw AppException.Invalid(nameof(r.Key), "Não pode enviar KWiK para si próprio.");

        return await ExecuteAsync(key, new Move(r.FromAccountId, r.Pin, TransactionKind.Transfer, r.Amount, 0, target.AccountId,
            TextSanitizer.CleanOrNull(r.Description) ?? "KWiK", target.FullName, target.Iban, null, "transfer.kwik"), ct);
    }

    public async Task<TransactionReceipt> GetReceiptAsync(Guid transactionId, CancellationToken ct)
    {
        var customerId = ctx.RequireCustomerId();
        var mine = db.Accounts.Where(a => a.CustomerId == customerId).Select(a => a.Id);
        var tx = await db.Transactions.AsNoTracking().Include(t => t.Entries)
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.Entries.Any(e => mine.Contains(e.AccountId)), ct)
            ?? throw AppException.NotFound("Transacção", feminine: true);
        var myIds = await mine.ToListAsync(ct);
        var entry = tx.Entries.First(e => myIds.Contains(e.AccountId));
        return ToReceipt(tx, entry.BalanceAfter, entry.Direction);
    }

    // ---------- Core: idempotent, PIN-authorised, limit-checked, atomic ----------

    private async Task<TransactionReceipt> ExecuteAsync(Guid key, Move m, CancellationToken ct)
    {
        var customerId = ctx.RequireCustomerId();
        var requestHash = Fingerprint(m);

        if (await FindReplayAsync(customerId, key, requestHash, ct) is { } replay) return replay;

        await VerifyPinAsync(customerId, m.Pin, m.AuditAction, ct);

        try
        {
            await using var dbTx = await db.Database.BeginTransactionAsync(ct);

            // Serialise money movements per customer: makes limit checks and idempotency race-free.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM customers WHERE id = {customerId} FOR UPDATE", ct);

            if (await FindReplayAsync(customerId, key, requestHash, ct) is { } replayAfterLock) return replayAfterLock;

            var source = await db.Accounts.AsNoTracking()
                .Where(a => a.Id == m.FromAccountId && a.CustomerId == customerId)
                .Select(a => new { a.Id, a.Status, a.Currency, Holder = a.Customer!.FullName })
                .FirstOrDefaultAsync(ct) ?? throw AppException.NotFound("Conta de origem", feminine: true);

            await EnforceLimitsAsync(customerId, m.Amount + m.Fee, ct);

            var legs = new List<PostingLeg> { new(source.Id, LedgerDirection.Debit, m.Amount + m.Fee), new(m.CreditAccountId, LedgerDirection.Credit, m.Amount) };
            if (m.Fee > 0) legs.Add(new(SystemAccounts.Fees, LedgerDirection.Credit, m.Fee));

            var tx = await poster.PostAsync(new LedgerPoster.NewTransaction(customerId, key, requestHash, m.Kind, m.Amount, m.Fee,
                m.Description, source.Holder, m.CounterpartyName, m.CounterpartyIban, m.ExternalReference), LedgerPosting.Create(legs), ct);

            await dbTx.CommitAsync(ct);
            await audit.RecordAsync(m.AuditAction, true, customerId, $"ref={tx.Reference} amount={m.Amount}");
            return ToReceipt(tx, tx.Entries.First(e => e.Direction == LedgerDirection.Debit).BalanceAfter);
        }
        catch (AppException ex)
        {
            await audit.RecordAsync(m.AuditAction, false, customerId, ex.Code);
            throw;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Backstop for the idempotency unique index.
            db.ChangeTracker.Clear();
            return await FindReplayAsync(customerId, key, requestHash, ct) ?? throw AppException.Conflict("Operação duplicada.");
        }
    }

    private async Task<TransactionReceipt?> FindReplayAsync(Guid customerId, Guid key, string requestHash, CancellationToken ct)
    {
        var existing = await db.Transactions.AsNoTracking().Include(t => t.Entries)
            .FirstOrDefaultAsync(t => t.InitiatedBy == customerId && t.IdempotencyKey == key, ct);
        if (existing is null) return null;
        if (existing.RequestHash != requestHash)
            throw new AppException(ErrorCodes.IdempotencyMismatch, "Esta chave de idempotência já foi usada noutra operação.", 422);
        return ToReceipt(existing, existing.Entries.First(e => e.Direction == LedgerDirection.Debit).BalanceAfter);
    }

    private async Task VerifyPinAsync(Guid customerId, string pin, string action, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var c = await db.Customers.AsNoTracking().Where(x => x.Id == customerId)
            .Select(x => new { x.PinHash, x.PinLockoutEndsAt, x.Status }).FirstOrDefaultAsync(ct)
            ?? throw new AppException(ErrorCodes.Unauthorized, "Sessão inválida.", 401);

        if (c.Status != CustomerStatus.Active) throw new AppException(ErrorCodes.Forbidden, "Conta indisponível.", 403);
        if (c.PinLockoutEndsAt > now)
            throw new AppException(ErrorCodes.PinLocked, "PIN bloqueado temporariamente. Tente mais tarde.", 423);

        if (!hasher.Verify(pin, c.PinHash))
        {
            await attempts.RegisterPinFailureAsync(customerId, ct);
            await audit.RecordAsync(action, false, customerId, "bad-pin");
            throw new AppException(ErrorCodes.InvalidPin, "PIN incorrecto.", 422);
        }
        await attempts.ResetPinAsync(customerId, ct);
    }

    private async Task EnforceLimitsAsync(Guid customerId, decimal debitTotal, CancellationToken ct)
    {
        if (debitTotal > _bank.PerTransferLimit)
            throw new AppException(ErrorCodes.LimitExceeded, $"O limite por operação é {Kz(_bank.PerTransferLimit)}.", 422);

        var now = clock.GetUtcNow().ToOffset(Wat);
        var dayStart = new DateTimeOffset(now.Date, Wat).ToUniversalTime();
        var spent = await db.Transactions.AsNoTracking()
            .Where(t => t.InitiatedBy == customerId && t.CreatedAt >= dayStart && t.Status == TransactionStatus.Completed
                        && t.Kind != TransactionKind.Deposit) // only customer-initiated outflows count
            .Select(t => t.Amount + t.Fee).SumAsync(ct);
        if (spent + debitTotal > _bank.DailyLimit)
            throw new AppException(ErrorCodes.LimitExceeded, $"Excede o limite diário de {Kz(_bank.DailyLimit)}.", 422);
    }

    private static string Kz(decimal v) => v.ToString("N0", PtAo) + " Kz";
    private static readonly System.Globalization.CultureInfo PtAo = new("pt-PT");

    private static string Fingerprint(Move m) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{m.Kind}|{m.FromAccountId}|{m.Amount}|{m.CreditAccountId}|{m.CounterpartyIban}|{m.ExternalReference}|{m.Description}")));

    private static TransactionReceipt ToReceipt(LedgerTransaction t, decimal balanceAfter, LedgerDirection direction = LedgerDirection.Debit) => new(
        t.Id, t.Reference, t.Kind, t.Status, t.Amount, t.Fee, t.Currency, t.Description, t.CounterpartyName,
        t.CounterpartyIban, balanceAfter, t.CreatedAt, direction, t.OriginatorName);
}

using System.Security.Cryptography;
using System.Text;
using BfaNet.Application;
using BfaNet.Application.Abstractions;
using BfaNet.Application.Assistant;
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

/// <summary>
/// Reads the customer's own ledger, runs the deterministic analyser and scoring rules, and (only after a PIN-confirmed,
/// idempotent request) moves microcredit money through the same double-entry ledger as every other operation.
/// Nothing the client sends is trusted: offers, limits, rates and instalments are recomputed here each time.
/// </summary>
public sealed class AssistantService(
    BankDbContext db, IRequestContext ctx, LedgerPoster poster, PinAuthorizer pins, IAuditWriter audit,
    IOptions<AssistantOptions> options, TimeProvider clock) : IAssistantService
{
    private static readonly TimeSpan Wat = TimeSpan.FromHours(1);
    private const int MaxRows = 5000;
    private readonly AssistantOptions _o = options.Value;

    private sealed record Analysis(AnalysisResult Result, DateOnly? FirstActivity, decimal TotalBalance);

    // ---------- Insights ----------

    public async Task<AssistantInsights> InsightsAsync(CancellationToken ct)
    {
        var a = await AnalyzeAsync(ct);
        var offer = await OfferFromAsync(a, ct);
        var extra = CreditScoring.OfferInsight(offer);
        var insights = extra is null ? a.Result.Insights : a.Result.Insights with { Insights = [.. a.Result.Insights.Insights, extra] };
        return insights;
    }

    private async Task<Analysis> AnalyzeAsync(CancellationToken ct)
    {
        var me = ctx.RequireCustomerId();
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.CustomerId == me && a.Status != AccountStatus.Closed && a.Currency == Currency.AOA)
            .OrderBy(a => a.OpenedAt).Select(a => new { a.Id, a.Type, a.Iban, a.Balance, a.Status }).ToListAsync(ct);
        var ids = accounts.Select(a => a.Id).ToList();

        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(Wat).DateTime);
        var twoMonthsAgo = today.AddMonths(-2);
        var windowStart = new DateTimeOffset(new DateOnly(twoMonthsAgo.Year, twoMonthsAgo.Month, 1).ToDateTime(TimeOnly.MinValue), Wat).ToUniversalTime();

        var raw = await db.LedgerEntries.AsNoTracking()
            .Where(e => ids.Contains(e.AccountId) && e.CreatedAt >= windowStart)
            .OrderBy(e => e.Id).Take(MaxRows)
            .Select(e => new
            {
                e.TransactionId, e.AccountId, e.Direction, e.Amount, e.CreatedAt, e.Transaction!.Kind, e.Transaction.Fee,
                e.Transaction.Description, e.Transaction.CounterpartyName, e.Transaction.OriginatorName, e.Transaction.ExternalReference,
            }).ToListAsync(ct);

        // A transaction touching two of the customer's own accounts is a move between pockets (e.g. into savings), not spending.
        var internalTx = raw.GroupBy(r => r.TransactionId).Where(g => g.Select(x => x.AccountId).Distinct().Count() >= 2).Select(g => g.Key).ToHashSet();
        var movements = raw.Select(r => new Movement(r.CreatedAt, r.Kind, r.Direction, r.Amount, r.Direction == LedgerDirection.Debit ? r.Fee : 0m, r.Description,
            r.Direction == LedgerDirection.Debit ? r.CounterpartyName : r.OriginatorName, r.ExternalReference, internalTx.Contains(r.TransactionId))).ToList();

        var first = await db.LedgerEntries.AsNoTracking().Where(e => ids.Contains(e.AccountId)).MinAsync(e => (DateTimeOffset?)e.CreatedAt, ct);
        var main = accounts.FirstOrDefault(a => a.Type is AccountType.Ordem or AccountType.Ordenado or AccountType.Bankita && a.Status == AccountStatus.Active);
        var savings = accounts.FirstOrDefault(a => a.Type == AccountType.Poupanca && a.Status == AccountStatus.Active);
        var spendable = accounts.Where(a => a.Type != AccountType.Poupanca).Sum(a => a.Balance);
        var savingsBalance = accounts.Where(a => a.Type == AccountType.Poupanca).Sum(a => a.Balance);

        var result = SpendingAnalyzer.Analyze(new AnalyzerInput(movements, today, spendable, savingsBalance, main?.Id, savings?.Id, savings?.Iban), clock.GetUtcNow());
        return new Analysis(result, first is null ? null : DateOnly.FromDateTime(first.Value.ToOffset(Wat).DateTime), spendable + savingsBalance);
    }

    // ---------- Credit ----------

    public async Task<CreditOfferDto> CreditOfferAsync(CancellationToken ct) => await OfferFromAsync(await AnalyzeAsync(ct), ct);

    private async Task<CreditOfferDto> OfferFromAsync(Analysis a, CancellationToken ct)
    {
        var me = ctx.RequireCustomerId();
        var active = await db.Loans.AsNoTracking().Include(l => l.Installments).FirstOrDefaultAsync(l => l.CustomerId == me && l.Status == LoanStatus.Active, ct);
        var i = a.Result.Insights;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(Wat).DateTime);
        var days = a.FirstActivity is { } f ? Math.Max(0, today.DayNumber - f.DayNumber) : 0;
        var profile = new CreditProfile(days, i.AverageMonthlyIncome, i.AverageMonthlySpend, a.Result.IncomeMonths, i.Health.Score, a.TotalBalance, active is not null);
        return CreditScoring.Evaluate(profile, _o, active is null ? null : ToDto(active));
    }

    public async Task<CreditSimulationDto> SimulateAsync(decimal amount, int months, CancellationToken ct)
    {
        var offer = await CreditOfferAsync(ct);
        Validate(offer, amount, months);
        return CreditScoring.Simulate(amount, months, offer);
    }

    private void Validate(CreditOfferDto offer, decimal amount, int months)
    {
        if (!offer.Eligible) throw new AppException(ErrorCodes.Conflict, offer.Blockers.FirstOrDefault() ?? "Não tem uma oferta disponível.", 409);
        if (!_o.Terms.Contains(months)) throw AppException.Invalid("months", $"Prazos disponíveis: {string.Join(", ", _o.Terms)} meses.");
        if (amount < offer.MinAmount || amount > offer.MaxAmount || decimal.Round(amount, 2) != amount)
            throw AppException.Invalid("amount", $"O montante deve estar entre {offer.MinAmount:N0} e {offer.MaxAmount:N0} Kz.");
        if (!CreditScoring.Simulate(amount, months, offer).WithinCapacity)
            throw AppException.Invalid("months", "A prestação excede a sua capacidade de pagamento. Escolha um prazo mais longo ou um montante menor.");
    }

    public async Task<LoanDto> AcceptCreditAsync(Guid key, AcceptCreditRequest r, CancellationToken ct)
    {
        var me = ctx.RequireCustomerId();
        var hash = Hash($"loan|{r.AccountId}|{r.Amount}|{r.Months}");

        if (await FindLoanByKeyAsync(me, key, hash, ct) is { } replay) return replay;
        await pins.VerifyAsync(me, r.Pin, "credit.accept", ct);

        try
        {
            await using var dbTx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM customers WHERE id = {me} FOR UPDATE", ct); // serialise per customer
            if (await FindLoanByKeyAsync(me, key, hash, ct) is { } replay2) return replay2;

            var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == r.AccountId && a.CustomerId == me && a.Status == AccountStatus.Active && a.Currency == Currency.AOA, ct)
                ?? throw AppException.NotFound("Conta", feminine: true);

            var offer = await OfferFromAsync(await AnalyzeAsync(ct), ct);
            Validate(offer, r.Amount, r.Months);
            var sim = CreditScoring.Simulate(r.Amount, r.Months, offer);

            var legs = new List<PostingLeg> { new(SystemAccounts.CreditPortfolio, LedgerDirection.Debit, r.Amount), new(account.Id, LedgerDirection.Credit, r.Amount - sim.Fee) };
            if (sim.Fee > 0) legs.Add(new(SystemAccounts.Fees, LedgerDirection.Credit, sim.Fee));
            var name = await db.Customers.AsNoTracking().Where(c => c.Id == me).Select(c => c.FullName).SingleAsync(ct);
            var ledgerTx = await poster.PostAsync(new LedgerPoster.NewTransaction(me, key, hash, TransactionKind.Loan, r.Amount, sim.Fee,
                $"Microcrédito de {r.Months} meses", "BFA", name, null, null), LedgerPosting.Create(legs), ct);

            var now = clock.GetUtcNow();
            var start = DateOnly.FromDateTime(now.ToOffset(Wat).DateTime);
            var loan = new Loan
            {
                CustomerId = me, AccountId = account.Id, Principal = r.Amount, TermMonths = r.Months, AnnualRatePercent = sim.AnnualRatePercent,
                OriginationFee = sim.Fee, Installment = sim.Installment, TotalRepayable = sim.TotalRepayable, DisbursementTransactionId = ledgerTx.Id, CreatedAt = now,
            };
            var due = LoanMath.DueDates(start, r.Months);
            for (var i = 0; i < due.Count; i++) loan.Installments.Add(new LoanInstallment { LoanId = loan.Id, Number = i + 1, DueDate = due[i], Amount = sim.Installment });
            db.Loans.Add(loan);
            await db.SaveChangesAsync(ct);
            await dbTx.CommitAsync(ct);
            await audit.RecordAsync("credit.accept", true, me, $"loan={loan.Id} amount={r.Amount} months={r.Months}");
            return ToDto(loan);
        }
        catch (AppException ex) { await audit.RecordAsync("credit.accept", false, me, ex.Code); throw; }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear(); // second active loan (DB index) or a racing duplicate of the same key
            return await FindLoanByKeyAsync(me, key, hash, ct) ?? throw AppException.Conflict("Já tem um microcrédito activo.");
        }
    }

    private async Task<LoanDto?> FindLoanByKeyAsync(Guid me, Guid key, string hash, CancellationToken ct)
    {
        var tx = await db.Transactions.AsNoTracking().FirstOrDefaultAsync(t => t.InitiatedBy == me && t.IdempotencyKey == key, ct);
        if (tx is null) return null;
        if (tx.RequestHash != hash) throw new AppException(ErrorCodes.IdempotencyMismatch, "Esta chave de idempotência já foi usada noutra operação.", 422);
        var loan = await db.Loans.AsNoTracking().Include(l => l.Installments).FirstOrDefaultAsync(l => l.DisbursementTransactionId == tx.Id, ct);
        return loan is null ? null : ToDto(loan);
    }

    // ---------- Loans & instalments ----------

    public async Task<IReadOnlyList<LoanDto>> LoansAsync(CancellationToken ct)
    {
        var me = ctx.RequireCustomerId();
        var loans = await db.Loans.AsNoTracking().Include(l => l.Installments).Where(l => l.CustomerId == me).OrderByDescending(l => l.CreatedAt).ToListAsync(ct);
        return loans.Select(ToDto).ToList();
    }

    public async Task<TransactionReceipt> RepayAsync(Guid key, Guid loanId, RepayLoanRequest r, CancellationToken ct)
    {
        var me = ctx.RequireCustomerId();
        var hash = Hash($"repay|{loanId}|{r.FromAccountId}");

        var existing = await db.Transactions.AsNoTracking().Include(t => t.Entries).FirstOrDefaultAsync(t => t.InitiatedBy == me && t.IdempotencyKey == key, ct);
        if (existing is not null)
        {
            if (existing.RequestHash != hash) throw new AppException(ErrorCodes.IdempotencyMismatch, "Esta chave de idempotência já foi usada noutra operação.", 422);
            return ReceiptMapper.From(existing, existing.Entries.First(e => e.Direction == LedgerDirection.Debit).BalanceAfter);
        }

        await pins.VerifyAsync(me, r.Pin, "credit.repay", ct);
        try
        {
            await using var dbTx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM customers WHERE id = {me} FOR UPDATE", ct);

            var loan = await db.Loans.Include(l => l.Installments).FirstOrDefaultAsync(l => l.Id == loanId && l.CustomerId == me, ct) ?? throw AppException.NotFound("Microcrédito");
            if (loan.Status != LoanStatus.Active) throw AppException.Conflict("Este microcrédito já está liquidado.");
            var next = loan.Installments.Where(i => i.PaidAt is null).OrderBy(i => i.Number).First();
            var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == r.FromAccountId && a.CustomerId == me && a.Status == AccountStatus.Active, ct)
                ?? throw AppException.NotFound("Conta de origem", feminine: true);

            var name = await db.Customers.AsNoTracking().Where(c => c.Id == me).Select(c => c.FullName).SingleAsync(ct);
            var ledgerTx = await poster.PostAsync(new LedgerPoster.NewTransaction(me, key, hash, TransactionKind.LoanRepayment, next.Amount, 0,
                $"Prestação {next.Number}/{loan.TermMonths} do microcrédito", name, "BFA Microcrédito", null, null),
                LedgerPosting.Create([new(account.Id, LedgerDirection.Debit, next.Amount), new(SystemAccounts.CreditPortfolio, LedgerDirection.Credit, next.Amount)]), ct);

            var now = clock.GetUtcNow();
            next.PaidAt = now;
            next.PaymentTransactionId = ledgerTx.Id;
            if (loan.Installments.All(i => i.PaidAt is not null)) { loan.Status = LoanStatus.Paid; loan.ClosedAt = now; }
            await db.SaveChangesAsync(ct);
            await dbTx.CommitAsync(ct);
            await audit.RecordAsync("credit.repay", true, me, $"loan={loan.Id} n={next.Number}");
            return ReceiptMapper.From(ledgerTx, ledgerTx.Entries.First(e => e.Direction == LedgerDirection.Debit).BalanceAfter);
        }
        catch (AppException ex) { await audit.RecordAsync("credit.repay", false, me, ex.Code); throw; }
    }

    // ---------- helpers ----------

    private static LoanDto ToDto(Loan l) => new(l.Id, l.Principal, l.TermMonths, l.AnnualRatePercent, l.Installment, l.TotalRepayable,
        l.Installments.Where(i => i.PaidAt is null).Sum(i => i.Amount), l.Status.ToString(), l.CreatedAt,
        l.Installments.OrderBy(i => i.Number).Select(i => new InstallmentDto(i.Number, i.DueDate.ToString("yyyy-MM-dd"), i.Amount, i.PaidAt)).ToList());

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}

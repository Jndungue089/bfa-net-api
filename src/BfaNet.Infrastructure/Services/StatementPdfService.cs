using BfaNet.Application.Abstractions;
using BfaNet.Application.Common;
using BfaNet.Domain;
using BfaNet.Infrastructure.Persistence;
using BfaNet.Infrastructure.Services.Pdf;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using static BfaNet.Infrastructure.Services.Pdf.PdfKit;

namespace BfaNet.Infrastructure.Services;

/// <summary>
/// Bank statement as a PDF, built on the server from the ledger: logo, holder and account, period, opening/closing
/// balance, totals over the whole period and a paginated table of movements (header repeated on every page).
/// </summary>
public sealed class StatementPdfService(BankDbContext db, IRequestContext ctx, TimeProvider clock) : IStatementPdfService
{
    private static readonly TimeSpan Wat = TimeSpan.FromHours(1);
    private const int MaxRows = 1000;
    private const int MaxDays = 366;

    static StatementPdfService() => PdfKit.Init();

    private sealed record Row(long Id, DateTimeOffset At, TransactionKind Kind, LedgerDirection Direction, decimal Amount, decimal BalanceAfter,
        string? Description, string? Counterparty, string? Originator);

    public async Task<ReceiptPdf> BuildAsync(Guid accountId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var me = ctx.RequireCustomerId();
        // Same 404 for "missing" and "someone else's".
        var account = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId && a.CustomerId == me)
            .Select(a => new { a.Iban, a.Type, a.Nickname, a.Currency, a.OpenedAt }).FirstOrDefaultAsync(ct)
            ?? throw AppException.NotFound("Conta", feminine: true);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(Wat).DateTime);
        var end = to ?? today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1);
        if (start > end) throw AppException.Invalid("from", "A data inicial deve ser anterior à final.");
        if (end > today) throw AppException.Invalid("to", "A data não pode ser futura.");
        if (end.DayNumber - start.DayNumber > MaxDays) throw AppException.Invalid("to", $"O período máximo é de {MaxDays} dias.");

        var startUtc = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), Wat).ToUniversalTime();
        var endUtc = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), Wat).ToUniversalTime();

        var inPeriod = db.LedgerEntries.AsNoTracking().Where(e => e.AccountId == accountId && e.CreatedAt >= startUtc && e.CreatedAt < endUtc);

        // Totals cover the whole period even when the table is capped.
        var totals = await inPeriod.GroupBy(e => e.Direction).Select(g => new { g.Key, Sum = g.Sum(e => e.Amount), Count = g.Count() }).ToListAsync(ct);
        var credits = totals.FirstOrDefault(t => t.Key == LedgerDirection.Credit)?.Sum ?? 0;
        var debits = totals.FirstOrDefault(t => t.Key == LedgerDirection.Debit)?.Sum ?? 0;
        var count = totals.Sum(t => t.Count);

        // Newest MaxRows movements, shown oldest → newest.
        var rows = (await inPeriod.OrderByDescending(e => e.Id).Take(MaxRows)
            .Select(e => new Row(e.Id, e.CreatedAt, e.Transaction!.Kind, e.Direction, e.Amount, e.BalanceAfter,
                e.Transaction.Description, e.Transaction.CounterpartyName, e.Transaction.OriginatorName)).ToListAsync(ct))
            .OrderBy(r => r.Id).ToList();

        var opening = await db.LedgerEntries.AsNoTracking().Where(e => e.AccountId == accountId && e.CreatedAt < startUtc)
            .OrderByDescending(e => e.Id).Select(e => (decimal?)e.BalanceAfter).FirstOrDefaultAsync(ct) ?? 0m;
        var closing = await db.LedgerEntries.AsNoTracking().Where(e => e.AccountId == accountId && e.CreatedAt < endUtc)
            .OrderByDescending(e => e.Id).Select(e => (decimal?)e.BalanceAfter).FirstOrDefaultAsync(ct) ?? opening;

        var holder = await db.Customers.AsNoTracking().Where(c => c.Id == me)
            .Select(c => new { c.FullName, c.CustomerNumber, c.NationalId }).SingleAsync(ct);

        var currency = account.Currency.ToString();
        var pdf = Compose(holder.FullName, holder.CustomerNumber, TextSanitizer.MaskNationalId(holder.NationalId), account.Iban,
            AccountLabel(account.Type, account.Nickname), currency, start, end, opening, closing, credits, debits, count, rows, truncated: count > rows.Count);
        return new ReceiptPdf(pdf.GeneratePdf(), $"extracto-{start:yyyyMMdd}_{end:yyyyMMdd}.pdf");
    }

    private Document Compose(string holder, string customerNumber, string idMasked, string iban, string accountLabel, string currency, DateOnly from, DateOnly to,
        decimal opening, decimal closing, decimal credits, decimal debits, int count, List<Row> rows, bool truncated) =>
        Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4);
            PageDefaults(page);
            Header(page, "Extracto bancário", When(clock.GetUtcNow()));

            page.Content().PaddingTop(14).Column(col =>
            {
                col.Spacing(12);

                col.Item().Row(r =>
                {
                    r.RelativeItem().Border(1).BorderColor(Line).CornerRadius(8).Padding(10).Column(b =>
                    {
                        b.Item().Text("TITULAR").FontSize(9).Bold().FontColor(Orange);
                        b.Item().PaddingTop(2).Text(holder).SemiBold();
                        b.Item().Text($"Nº de adesão {customerNumber} · BI {idMasked}").FontSize(10).FontColor(Muted);
                    });
                    r.ConstantItem(10);
                    r.RelativeItem().Border(1).BorderColor(Line).CornerRadius(8).Padding(10).Column(b =>
                    {
                        b.Item().Text("CONTA").FontSize(9).Bold().FontColor(Orange);
                        b.Item().PaddingTop(2).Text(accountLabel).SemiBold();
                        b.Item().Text(Iban.Format(iban)).FontSize(10).FontColor(Muted);
                    });
                });

                col.Item().Text(t =>
                {
                    t.Span("Período: ").FontColor(Muted);
                    t.Span($"{Day(from)} a {Day(to)}").SemiBold();
                });

                col.Item().Row(r =>
                {
                    Box(r, "Saldo inicial", Money(opening, currency), Ink);
                    r.ConstantItem(8);
                    Box(r, "Entradas", Money(credits, currency, sign: true), Green);
                    r.ConstantItem(8);
                    Box(r, "Saídas", Money(-debits, currency), Ink);
                    r.ConstantItem(8);
                    Box(r, "Saldo final", Money(closing, currency), Navy);
                });

                col.Item().Text($"{count} movimento{(count == 1 ? "" : "s")}").FontSize(10).FontColor(Muted);

                if (rows.Count == 0)
                {
                    col.Item().PaddingVertical(20).AlignCenter().Text("Sem movimentos no período.").FontColor(Muted);
                    return;
                }

                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(d => { d.ConstantColumn(62); d.RelativeColumn(5); d.RelativeColumn(2.6f); d.RelativeColumn(2.6f); });
                    t.Header(h =>
                    {
                        HeadCell(h.Cell(), "Data"); HeadCell(h.Cell(), "Descrição"); HeadCell(h.Cell(), "Movimento", right: true); HeadCell(h.Cell(), "Saldo", right: true);
                    });
                    foreach (var r in rows)
                    {
                        var credit = r.Direction == LedgerDirection.Credit;
                        var who = credit ? r.Originator : r.Counterparty;
                        Body(t.Cell()).Text(Day(r.At)).FontSize(10);
                        Body(t.Cell()).Column(x =>
                        {
                            x.Item().Text(KindLabel(r.Kind)).SemiBold().FontSize(10.5f);
                            var detail = string.Join(" · ", new[] { who, r.Description }.Where(s => !string.IsNullOrWhiteSpace(s)));
                            if (detail.Length > 0) x.Item().Text(detail).FontSize(9.5f).FontColor(Muted);
                        });
                        // Statements are where signs belong: credits +, debits −.
                        Body(t.Cell()).AlignRight().Text(Money(credit ? r.Amount : -r.Amount, currency, sign: true)).FontSize(10).SemiBold().FontColor(credit ? Green : Ink);
                        Body(t.Cell()).AlignRight().Text(Money(r.BalanceAfter, currency)).FontSize(10).FontColor(Muted);
                    }
                });

                if (truncated) col.Item().Text($"Tabela limitada aos {rows.Count} movimentos mais recentes do período; os totais consideram todos.").FontSize(9.5f).FontColor(Muted);
            });

            Footer(page, "Documento gerado electronicamente pelo BFA NET, sem valor contabilístico.",
                "Projecto de demonstração — não é um documento oficial do Banco de Fomento Angola.");
        }));

    private static void Box(QuestPDF.Fluent.RowDescriptor r, string label, string value, string color) =>
        r.RelativeItem().Border(1).BorderColor(Line).CornerRadius(8).Padding(9).Column(b =>
        {
            b.Item().Text(label).FontSize(9.5f).FontColor(Muted);
            b.Item().PaddingTop(2).Text(value).FontSize(12).Bold().FontColor(color);
        });

    private static void HeadCell(QuestPDF.Infrastructure.IContainer cell, string text, bool right = false)
    {
        var c = cell.Background(Navy).PaddingVertical(6).PaddingHorizontal(6);
        (right ? c.AlignRight() : c).Text(text).FontSize(10).SemiBold().FontColor(Colors.White);
    }

    private static QuestPDF.Infrastructure.IContainer Body(QuestPDF.Infrastructure.IContainer cell) =>
        cell.ShowEntire().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(5).PaddingHorizontal(6); // never split a row across pages

    private static string AccountLabel(AccountType type, string? nickname) =>
        nickname ?? type switch { AccountType.Ordem => "Conta à Ordem", AccountType.Ordenado => "Conta Ordenado", AccountType.Poupanca => "Conta Poupança", AccountType.Bankita => "Conta Bankita", _ => "Conta" };

    private static string KindLabel(TransactionKind k) => k switch
    {
        TransactionKind.Transfer => "Transferência", TransactionKind.ServicePayment => "Pagamento de serviços", TransactionKind.TopUp => "Carregamento / fornecedor",
        TransactionKind.StatePayment => "Pagamento ao Estado", TransactionKind.Deposit => "Depósito", _ => "Comissão",
    };
}

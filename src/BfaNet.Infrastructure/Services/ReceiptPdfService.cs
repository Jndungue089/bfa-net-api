using BfaNet.Application.Abstractions;
using BfaNet.Application.Common;
using BfaNet.Domain;
using BfaNet.Infrastructure.Persistence;
using BfaNet.Infrastructure.Services.Pdf;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using static BfaNet.Infrastructure.Services.Pdf.PdfKit;

namespace BfaNet.Infrastructure.Services;

/// <summary>
/// Builds the transaction receipt as a PDF on the server, so what a customer shares is a document issued by the
/// bank. Privacy rule: the payer's identity details (customer number, masked ID and phone, account) are printed
/// only for the payer; a payee receives the payer's name and nothing else. The account balance is never printed.
/// </summary>
public sealed class ReceiptPdfService(BankDbContext db, IRequestContext ctx, TimeProvider clock) : IReceiptPdfService
{
    static ReceiptPdfService() => PdfKit.Init();

    public async Task<ReceiptPdf> BuildAsync(Guid transactionId, CancellationToken ct)
    {
        var me = ctx.RequireCustomerId();
        var mine = db.Accounts.Where(a => a.CustomerId == me).Select(a => a.Id);
        var tx = await db.Transactions.AsNoTracking().Include(t => t.Entries)
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.Entries.Any(e => mine.Contains(e.AccountId)), ct)
            ?? throw AppException.NotFound("Transacção", feminine: true); // same 404 for "missing" and "someone else's"

        // A deposit is always money arriving, whoever the seed/back-office recorded as initiator.
        var payer = tx.InitiatedBy == me && tx.Kind != TransactionKind.Deposit;
        var debit = tx.Entries.First(e => e.Direction == LedgerDirection.Debit);
        var myIds = await mine.ToListAsync(ct);
        var myEntry = tx.Entries.First(e => myIds.Contains(e.AccountId));

        // The account that had the transaction from the viewer's side: debited (payer) or credited (payee).
        var accountId = payer ? debit.AccountId : myEntry.AccountId;
        var account = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId)
            .Select(a => new { a.Iban, a.Type, a.Nickname, a.Currency }).SingleAsync(ct);
        var initiator = await db.Customers.AsNoTracking().Where(c => c.Id == tx.InitiatedBy)
            .Select(c => new { c.FullName, c.CustomerNumber, c.NationalId, c.Phone }).SingleAsync(ct);

        var doc = Compose(tx, payer, account.Iban, AccountLabel(account.Type, account.Nickname), account.Currency.ToString(),
            payer ? initiator.FullName : tx.OriginatorName ?? initiator.FullName,
            payer ? initiator.CustomerNumber : null, payer ? TextSanitizer.MaskNationalId(initiator.NationalId) : null,
            payer ? TextSanitizer.MaskPhone(initiator.Phone) : null);
        return new ReceiptPdf(doc.GeneratePdf(), $"comprovativo-{tx.Reference}.pdf");
    }

    private Document Compose(Domain.Entities.LedgerTransaction tx, bool payer, string accountIban, string accountLabel, string currency,
        string payerName, string? customerNumber, string? nationalIdMasked, string? phoneMasked)
    {
        var amount = Money(tx.Amount, currency);
        var status = tx.Status switch { TransactionStatus.Completed => "Concluída", TransactionStatus.Reversed => "Revertida", _ => "Falhada" };

        return Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4);
            PageDefaults(page);
            Header(page, "Comprovativo", When(clock.GetUtcNow()));

            page.Content().PaddingTop(18).Column(col =>
            {
                col.Spacing(14);

                col.Item().Border(1).BorderColor(Line).CornerRadius(8).Padding(16).Column(b =>
                {
                    b.Item().Text(KindLabel(tx.Kind)).FontColor(Muted);
                    b.Item().PaddingTop(2).Text(amount).FontSize(30).Bold().FontColor(Navy);
                    b.Item().PaddingTop(4).Text(status).SemiBold().FontColor(tx.Status == TransactionStatus.Completed ? "#059669" : "#DC2626");
                });

                Section(col, payer ? "Ordenante" : "Ordenante (nome)", s =>
                {
                    Row(s, "Nome", payerName);
                    if (customerNumber is not null) Row(s, "Nº de adesão", customerNumber);
                    if (nationalIdMasked is not null) Row(s, "BI", nationalIdMasked);
                    if (phoneMasked is not null) Row(s, "Telemóvel", phoneMasked);
                });

                Section(col, payer ? "Conta debitada" : "Conta creditada", s =>
                {
                    Row(s, "Conta", accountLabel);
                    Row(s, "IBAN", Iban.Format(accountIban));
                    Row(s, "Moeda", currency == "AOA" ? "Kwanza (AOA)" : currency);
                });

                if (payer)
                {
                    Section(col, "Destino", s =>
                    {
                        Row(s, "Beneficiário", tx.CounterpartyName);
                        if (tx.CounterpartyIban is { } iban) Row(s, "IBAN", Iban.Format(iban));
                        if (ServiceReference(tx) is { } sr) Row(s, sr.Label, sr.Value);
                    });
                }

                Section(col, "Detalhes da operação", s =>
                {
                    Row(s, "Descrição", tx.Description);
                    Row(s, "Montante", Money(tx.Amount, currency));
                    if (tx.Fee > 0) Row(s, "Comissão", Money(tx.Fee, currency));
                    if (payer && tx.Fee > 0) Row(s, "Total debitado", Money(tx.Amount + tx.Fee, currency));
                    Row(s, "Data e hora", When(tx.CreatedAt));
                    Row(s, "Referência", tx.Reference, mono: true);
                });
            });

            Footer(page, "Documento gerado electronicamente pelo BFA NET. Não contém o saldo da conta.",
                "Projecto de demonstração — não é um documento oficial do Banco de Fomento Angola.");
        }));
    }

    private static void Section(ColumnDescriptor col, string title, Action<ColumnDescriptor> body) =>
        col.Item().Column(s =>
        {
            s.Item().PaddingBottom(4).Text(title.ToUpperInvariant()).FontSize(9.5f).Bold().FontColor(Orange);
            s.Item().Border(1).BorderColor(Line).CornerRadius(8).PaddingHorizontal(14).PaddingVertical(4).Column(body);
        });

    private static void Row(ColumnDescriptor col, string label, string? value, bool mono = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        col.Item().PaddingVertical(5).BorderBottom(0.5f).BorderColor(Line).Row(r =>
        {
            r.RelativeItem(2).Text(label).FontColor(Muted);
            r.RelativeItem(3).AlignRight().Text(value).SemiBold().FontSize(mono ? 10.5f : 11.5f);
        });
    }

    private static (string Label, string Value)? ServiceReference(Domain.Entities.LedgerTransaction tx)
    {
        var x = tx.ExternalReference;
        if (string.IsNullOrEmpty(x)) return null;
        return tx.Kind switch
        {
            TransactionKind.ServicePayment => ("Entidade / referência", x.Replace("/", " / ")),
            TransactionKind.StatePayment => ("Referência de pagamento", x),
            TransactionKind.TopUp when x.Split(':', 2) is [var provider, var id] => (provider is "Ende" ? "Nº do contador" : provider is "Dstv" or "Zap" ? "Nº do subscritor" : "Telemóvel", id),
            _ => null,
        };
    }

    private static string AccountLabel(AccountType type, string? nickname) =>
        nickname ?? type switch { AccountType.Ordem => "Conta à Ordem", AccountType.Ordenado => "Conta Ordenado", AccountType.Poupanca => "Conta Poupança", AccountType.Bankita => "Conta Bankita", _ => "Conta" };

    private static string KindLabel(TransactionKind k) => k switch
    {
        TransactionKind.Transfer => "Transferência", TransactionKind.ServicePayment => "Pagamento de serviços", TransactionKind.TopUp => "Carregamento / pagamento a fornecedor",
        TransactionKind.StatePayment => "Pagamento ao Estado", TransactionKind.Deposit => "Depósito", _ => "Comissão",
    };
}

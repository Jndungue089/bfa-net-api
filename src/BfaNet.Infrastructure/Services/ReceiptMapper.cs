using BfaNet.Application.Contracts;
using BfaNet.Domain;
using BfaNet.Domain.Entities;

namespace BfaNet.Infrastructure.Services;

internal static class ReceiptMapper
{
    public static TransactionReceipt From(LedgerTransaction t, decimal balanceAfter, LedgerDirection direction = LedgerDirection.Debit) => new(
        t.Id, t.Reference, t.Kind, t.Status, t.Amount, t.Fee, t.Currency, t.Description, t.CounterpartyName,
        t.CounterpartyIban, balanceAfter, t.CreatedAt, direction, t.OriginatorName);
}

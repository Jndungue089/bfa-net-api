using BfaNet.Application.Abstractions;
using BfaNet.Application.Common;
using BfaNet.Application.Contracts;
using BfaNet.Domain;
using BfaNet.Domain.Entities;
using BfaNet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BfaNet.Infrastructure.Services;

public sealed class CardService(BankDbContext db, IRequestContext ctx, IAuditWriter audit) : ICardService
{
    public async Task<IReadOnlyList<CardDto>> ListAsync(CancellationToken ct)
    {
        var id = ctx.RequireCustomerId();
        var cards = await db.Cards.AsNoTracking().Where(c => c.CustomerId == id && c.Status != CardStatus.Cancelled)
            .OrderBy(c => c.CreatedAt).ToListAsync(ct);
        return cards.Select(ToDto).ToList();
    }

    public async Task<CardDto> UpdateAsync(Guid cardId, UpdateCardRequest r, CancellationToken ct)
    {
        var id = ctx.RequireCustomerId();
        var card = await db.Cards.FirstOrDefaultAsync(c => c.Id == cardId && c.CustomerId == id, ct)
            ?? throw AppException.NotFound("Cartão");
        if (card.Status == CardStatus.Cancelled) throw AppException.NotFound("Cartão");

        if (r.Blocked is { } blocked) card.Status = blocked ? CardStatus.Blocked : CardStatus.Active;
        if (r.OnlinePurchases is { } online) card.OnlinePurchases = online;
        if (r.Contactless is { } nfc) card.Contactless = nfc;
        if (r.AtmWithdrawals is { } atm) card.AtmWithdrawals = atm;
        if (r.InternationalPayments is { } intl) card.InternationalPayments = intl;
        if (r.DailyLimit is { } limit) card.DailyLimit = limit;

        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("card.update", true, id, $"card=…{card.Last4} status={card.Status}");
        return ToDto(card);
    }

    private static CardDto ToDto(Card c) => new(c.Id, c.AccountId, c.Product, c.ProductName, c.Last4, c.HolderName,
        c.ExpiryMonth, c.ExpiryYear, c.Status, c.OnlinePurchases, c.Contactless, c.AtmWithdrawals, c.InternationalPayments, c.DailyLimit);
}

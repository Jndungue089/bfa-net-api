using BfaNet.Application.Abstractions;
using BfaNet.Application.Common;
using BfaNet.Application.Contracts;
using BfaNet.Domain.Entities;
using BfaNet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BfaNet.Infrastructure.Services;

public sealed class BeneficiaryService(BankDbContext db, IRequestContext ctx, IAuditWriter audit, TimeProvider clock) : IBeneficiaryService
{
    private const int MaxPerCustomer = 100;

    public async Task<IReadOnlyList<BeneficiaryDto>> ListAsync(CancellationToken ct)
    {
        var id = ctx.RequireCustomerId();
        return await db.Beneficiaries.AsNoTracking().Where(b => b.CustomerId == id).OrderBy(b => b.Name)
            .Select(b => new BeneficiaryDto(b.Id, b.Name, b.Iban, b.BankName, b.CreatedAt)).ToListAsync(ct);
    }

    public async Task<BeneficiaryDto> CreateAsync(BeneficiaryRequest r, CancellationToken ct)
    {
        var id = ctx.RequireCustomerId();
        if (await db.Beneficiaries.CountAsync(b => b.CustomerId == id, ct) >= MaxPerCustomer)
            throw new AppException(ErrorCodes.LimitExceeded, "Atingiu o número máximo de beneficiários.", 422);

        var b = new Beneficiary
        {
            CustomerId = id, Name = TextSanitizer.Clean(r.Name), Iban = TextSanitizer.NormalizeIban(r.Iban),
            BankName = Iban.IsBfa(TextSanitizer.NormalizeIban(r.Iban)) ? "BFA" : null, CreatedAt = clock.GetUtcNow()
        };
        db.Beneficiaries.Add(b);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw AppException.Conflict("Este beneficiário já existe.");
        }
        await audit.RecordAsync("beneficiary.create", true, id, $"iban=…{b.Iban[^4..]}");
        return new BeneficiaryDto(b.Id, b.Name, b.Iban, b.BankName, b.CreatedAt);
    }

    public async Task DeleteAsync(Guid beneficiaryId, CancellationToken ct)
    {
        var id = ctx.RequireCustomerId();
        var n = await db.Beneficiaries.Where(b => b.Id == beneficiaryId && b.CustomerId == id).ExecuteDeleteAsync(ct);
        if (n == 0) throw AppException.NotFound("Beneficiário");
        await audit.RecordAsync("beneficiary.delete", true, id);
    }
}

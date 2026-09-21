using BfaNet.Application;
using BfaNet.Application.Abstractions;
using BfaNet.Application.Common;
using BfaNet.Domain;
using BfaNet.Domain.Entities;
using BfaNet.Domain.Ledger;
using BfaNet.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BfaNet.Infrastructure.Persistence;

public static class DataSeeder
{
    public const string DemoCustomerNumber = "10000001";

    /// <summary>Reference data required in every environment: GL accounts and FX rates.</summary>
    public static async Task SeedReferenceAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BankDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();

        foreach (var (id, number) in SystemAccounts.All)
        {
            if (await db.Accounts.AnyAsync(a => a.Id == id, ct)) continue;
            db.Accounts.Add(new Account
            {
                Id = id, AccountNumber = number, Iban = Iban.Generate("0000", number), Type = AccountType.Interna,
                Currency = Currency.AOA, OpenedAt = now
            });
        }

        if (!await db.ExchangeRates.AnyAsync(ct))
        {
            // Indicative reference values; the refresher overwrites EUR from bfa.ao when reachable.
            db.ExchangeRates.AddRange(
                new ExchangeRate { Currency = Currency.USD, Buy = 912.500m, Sell = 918.000m, Source = "Referência", UpdatedAt = now },
                new ExchangeRate { Currency = Currency.EUR, Buy = 186.302m, Sell = 191.891m, Source = "bfa.ao", UpdatedAt = now });
        }

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another replica seeded the same rows first (all-or-nothing per instance): nothing left to do.
        }
    }

    /// <summary>Development only: a demo customer with two accounts, a card and some history.</summary>
    public static async Task SeedDemoAsync(IServiceProvider sp, string password, string pin, CancellationToken ct = default)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BankDbContext>();
        if (await db.Customers.AnyAsync(c => c.CustomerNumber == DemoCustomerNumber, ct)) return;

        var hasher = scope.ServiceProvider.GetRequiredService<ISecretHasher>();
        var blind = scope.ServiceProvider.GetRequiredService<IBlindIndex>();
        var poster = scope.ServiceProvider.GetRequiredService<LedgerPoster>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var bank = scope.ServiceProvider.GetRequiredService<IOptions<BankingOptions>>().Value;
        var now = clock.GetUtcNow();

        var customer = new Customer
        {
            CustomerNumber = DemoCustomerNumber, FullName = "Maria Fernanda dos Santos",
            Email = "maria.santos@example.ao", EmailHash = blind.Compute("email", "maria.santos@example.ao"),
            Phone = "923456789", PhoneHash = blind.Compute("phone", "923456789"),
            NationalId = "005678901LA041", NationalIdHash = blind.Compute("national_id", "005678901LA041"),
            BirthDate = new DateOnly(1994, 3, 12), PasswordHash = hasher.Hash(password), PinHash = hasher.Hash(pin),
            PasswordChangedAt = now, CreatedAt = now, UpdatedAt = now
        };
        var ordem = new Account { CustomerId = customer.Id, AccountNumber = "10000000001", Iban = Iban.Generate(bank.DefaultBranch, "10000000001"), Type = AccountType.Ordem, Nickname = "Conta principal", OpenedAt = now };
        var poupanca = new Account { CustomerId = customer.Id, AccountNumber = "10000000002", Iban = Iban.Generate(bank.DefaultBranch, "10000000002"), Type = AccountType.Poupanca, Nickname = "Poupança", OpenedAt = now };
        db.Customers.Add(customer);
        db.Accounts.AddRange(ordem, poupanca);
        db.Cards.Add(new Card { CustomerId = customer.Id, AccountId = ordem.Id, Product = CardProduct.Debito, ProductName = "Cartão de Débito BFA", Last4 = "4821", HolderName = "MARIA F SANTOS", ExpiryMonth = 9, ExpiryYear = now.Year + 3, DailyLimit = 300_000m, CreatedAt = now });
        db.Beneficiaries.Add(new Beneficiary { CustomerId = customer.Id, Name = "João Manuel Pereira", Iban = Iban.Generate("0001", "20000000001"), BankName = "BFA", CreatedAt = now });
        await db.SaveChangesAsync(ct);

        await SeedHistoryAsync(db, poster, customer, ordem.Id, poupanca.Id, now, ct);
    }

    /// <summary>
    /// ~100 days of believable activity so the financial assistant has something real to analyse: salary on the 27th, rent,
    /// utilities, TV, groceries, fuel, a recurring school fee, two interbank transfers (with fees) and one unusually big purchase
    /// this month. Deterministic (fixed seed) and posted chronologically through the ledger, so balances are always consistent.
    /// </summary>
    private static async Task SeedHistoryAsync(BankDbContext db, LedgerPoster poster, Customer customer, Guid main, Guid savings, DateTimeOffset now, CancellationToken ct)
    {
        var wat = TimeSpan.FromHours(1);
        var today = DateOnly.FromDateTime(now.ToOffset(wat).DateTime);
        var start = today.AddDays(-100);
        var rnd = new Random(7);

        async Task Move(DateOnly day, TransactionKind kind, decimal amount, string description, string counterparty, string? ext, Guid other, bool incoming, Guid account, decimal fee = 0)
        {
            var at = new DateTimeOffset(day.ToDateTime(new TimeOnly(10, 0)), wat).ToUniversalTime().AddMinutes(rnd.Next(0, 240));
            var legs = incoming
                ? new List<PostingLeg> { new(other, LedgerDirection.Debit, amount), new(account, LedgerDirection.Credit, amount) }
                : new List<PostingLeg> { new(account, LedgerDirection.Debit, amount + fee), new(other, LedgerDirection.Credit, amount) };
            if (!incoming && fee > 0) legs.Add(new(SystemAccounts.Fees, LedgerDirection.Credit, fee));
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await poster.PostAsync(new LedgerPoster.NewTransaction(customer.Id, Guid.NewGuid(), Guid.NewGuid().ToString("N"), kind, amount, fee, description,
                incoming ? counterparty : customer.FullName, incoming ? customer.FullName : counterparty, null, ext, at), LedgerPosting.Create(legs), ct);
            await tx.CommitAsync(ct);
        }

        Task Deposit(DateOnly d, decimal amount, string what, string from, Guid account) => Move(d, TransactionKind.Deposit, amount, what, from, null, SystemAccounts.CashVault, true, account);
        Task Pay(DateOnly d, decimal amount, string description, string who, string? ext = null, TransactionKind kind = TransactionKind.ServicePayment) =>
            Move(d, kind, amount, description, who, ext, kind == TransactionKind.TopUp ? SystemAccounts.TopUpSettlement : SystemAccounts.ServiceSettlement, false, main);

        await Deposit(start, 1_250_000m, "Depósito inicial", "BFA", main);
        await Deposit(start, 3_400_000m, "Depósito inicial", "BFA", savings);

        for (var d = start.AddDays(1); d <= today; d = d.AddDays(1))
        {
            var age = today.DayNumber - d.DayNumber;
            if (d.Day == 27) await Deposit(d, 450_000m, "Salário", "Empresa Alfa, Lda", main);
            if (d.Day == 3) await Pay(d, 120_000m, "Renda de casa", "Imobiliária Sol");
            if (d.Day == 5) await Pay(d, 35_000m, "Propina", "Colégio São José");
            if (d.Day == 12) await Pay(d, 15_000m, "Pagamento DStv", "DStv", "Dstv:1234567890", TransactionKind.TopUp);
            if (d.Day == 15) await Pay(d, Math.Round(11_000m + rnd.Next(0, 2500), 0), "Pagamento ENDE", "ENDE", "Ende:123456789012", TransactionKind.TopUp);
            if (d.Day is 8 or 22) await Pay(d, 5_000m, "Carregamento Unitel 923456789", "Unitel", "Unitel:923456789", TransactionKind.TopUp);
            if (d.DayNumber % 5 == 0) await Pay(d, Math.Round(9_000m + rnd.Next(0, 11_000), 0), "Compras do mês", "Kero Hipermercado", "11223/123456789");
            if (d.DayNumber % 7 == 1) await Pay(d, Math.Round(6_000m + rnd.Next(0, 4_000), 0), "Combustível", "Sonangol", "44551/987654321");
            if (d.DayNumber % 6 == 2) await Pay(d, Math.Round(4_000m + rnd.Next(0, 5_000), 0), "Almoço", "Restaurante Mama Zeca", "77889/111222333");
            if (age is 90 or 62) await Pay(d, 10_000m, "Compra - Loja do Zé", "Loja do Zé", "55667/444555666");
            if (age is 70 or 35) await Move(d, TransactionKind.Transfer, 12_000m, "Ajuda familiar", "Ana Costa", null, SystemAccounts.InterbankSettlement, false, main, fee: 150m);
            if (age == 4) await Pay(d, 65_000m, "Compra - Móveis & Decor", "Móveis & Decor", "99887/555444333");
        }
        await Task.CompletedTask;
    }
}

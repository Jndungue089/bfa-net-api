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

        // Opening balances go through the ledger like everything else (cash vault → customer).
        await Deposit(ordem.Id, 1_250_000m, "Depósito inicial");
        await Deposit(poupanca.Id, 3_400_000m, "Depósito inicial");
        await Deposit(ordem.Id, 450_000m, "Salário");

        async Task Deposit(Guid accountId, decimal amount, string description)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await poster.PostAsync(new LedgerPoster.NewTransaction(customer.Id, Guid.NewGuid(), Guid.NewGuid().ToString("N"), TransactionKind.Deposit,
                amount, 0, description, "BFA", "BFA", null, null),
                LedgerPosting.Create([new(SystemAccounts.CashVault, LedgerDirection.Debit, amount), new(accountId, LedgerDirection.Credit, amount)]), ct);
            await tx.CommitAsync(ct);
        }
    }
}

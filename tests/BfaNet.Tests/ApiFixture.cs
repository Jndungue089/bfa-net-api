using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BfaNet.Tests;

/// <summary>Runs only when BFANET_TEST_CONNECTION points at a disposable Postgres database.</summary>
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BFANET_TEST_CONNECTION")))
            Skip = "Defina BFANET_TEST_CONNECTION para correr os testes de integração.";
    }
}

public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static int _seq; // static: several test classes (each with its own server) share one database

    public ApiFixture()
    {
        var conn = Environment.GetEnvironmentVariable("BFANET_TEST_CONNECTION");
        if (string.IsNullOrEmpty(conn)) return;

        // Env vars are read by the host before Program runs, unlike per-test configuration callbacks.
        void Set(string k, string v) => Environment.SetEnvironmentVariable(k, v);
        Set("ASPNETCORE_ENVIRONMENT", "Development");
        Set("ConnectionStrings__Default", conn);
        Set("Security__EncryptionKeys__k1", Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()));
        Set("Security__BlindIndexKey", Convert.ToBase64String(new byte[48].Select((_, i) => (byte)(i + 7)).ToArray()));
        Set("Security__JwtSigningKey", Convert.ToBase64String(new byte[48].Select((_, i) => (byte)(i + 3)).ToArray()));
        Set("Seed__Demo", "false");
        Set("RateLimits__AuthPerMinute", "100000");
        Set("RateLimits__MoneyPerMinute", "100000");
        Set("RateLimits__GlobalPerMinute", "100000");
    }

    public Task InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    public sealed record Customer(HttpClient Client, string Number, string Password, string Pin, string AccessToken, string RefreshToken, Guid AccountId, string Iban, string Phone);

    /// <summary>Registers a customer (mobile mode → tokens in body) and tops the account up via the ledger.</summary>
    public async Task<Customer> RegisterAsync(decimal funds = 0)
    {
        var n = Interlocked.Increment(ref _seq);
        var rnd = Random.Shared;
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-BFA-Client", "mobile");
        var password = "Cofre#Forte-2026x";
        var pin = "739154";
        var phone = "9" + rnd.Next(10_000_000, 99_999_999);
        var body = new
        {
            fullName = $"Cliente Teste {(char)('A' + n % 26)}",
            email = $"t{Guid.NewGuid():N}@example.ao",
            phone,
            nationalId = $"{rnd.Next(100_000_000, 999_999_999)}LA{rnd.Next(100, 999)}",
            birthDate = "1990-01-01", password, pin, acceptTerms = true
        };
        var res = await client.PostAsJsonAsync("/api/v1/auth/register", body);
        Assert.Equal(System.Net.HttpStatusCode.Created, res.StatusCode);
        var session = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        var access = session.GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/v1/accounts", Json);
        var acc = accounts[0];
        var id = acc.GetProperty("id").GetGuid();
        if (funds > 0) await FundAsync(id, funds);
        return new Customer(client, session.GetProperty("profile").GetProperty("customerNumber").GetString()!, password, pin,
            access, session.GetProperty("refreshToken").GetString()!, id, acc.GetProperty("iban").GetString()!, phone);
    }

    private async Task FundAsync(Guid accountId, decimal amount)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BfaNet.Infrastructure.Persistence.BankDbContext>();
        var poster = scope.ServiceProvider.GetRequiredService<BfaNet.Infrastructure.Services.LedgerPoster>();
        var owner = db.Accounts.Where(a => a.Id == accountId).Select(a => a.CustomerId!.Value).Single();
        await using var tx = await db.Database.BeginTransactionAsync();
        await poster.PostAsync(new(owner, Guid.NewGuid(), Guid.NewGuid().ToString("N"), BfaNet.Domain.TransactionKind.Deposit, amount, 0, "Fundos de teste", "BFA", "BFA", null, null),
            BfaNet.Domain.Ledger.LedgerPosting.Create([
                new(BfaNet.Application.Common.SystemAccounts.CashVault, BfaNet.Domain.LedgerDirection.Debit, amount),
                new(accountId, BfaNet.Domain.LedgerDirection.Credit, amount)]), default);
        await tx.CommitAsync();
    }

    /// <summary>Posts a backdated movement through the real ledger (salary in, spending out) so the assistant has history to analyse.</summary>
    public async Task PostBackdatedAsync(Guid accountId, bool incoming, decimal amount, int daysAgo, BfaNet.Domain.TransactionKind kind, string description, string counterparty, string? ext = null)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BfaNet.Infrastructure.Persistence.BankDbContext>();
        var poster = scope.ServiceProvider.GetRequiredService<BfaNet.Infrastructure.Services.LedgerPoster>();
        var owner = db.Accounts.Where(a => a.Id == accountId).Select(a => a.CustomerId!.Value).Single();
        var other = incoming ? BfaNet.Application.Common.SystemAccounts.CashVault : BfaNet.Application.Common.SystemAccounts.ServiceSettlement;
        var legs = incoming
            ? new[] { new BfaNet.Domain.Ledger.PostingLeg(other, BfaNet.Domain.LedgerDirection.Debit, amount), new(accountId, BfaNet.Domain.LedgerDirection.Credit, amount) }
            : [new BfaNet.Domain.Ledger.PostingLeg(accountId, BfaNet.Domain.LedgerDirection.Debit, amount), new(other, BfaNet.Domain.LedgerDirection.Credit, amount)];
        await using var tx = await db.Database.BeginTransactionAsync();
        await poster.PostAsync(new(owner, Guid.NewGuid(), Guid.NewGuid().ToString("N"), kind, amount, 0, description, incoming ? counterparty : "Cliente", incoming ? "Cliente" : counterparty, null, ext,
            DateTimeOffset.UtcNow.AddDays(-daysAgo)), BfaNet.Domain.Ledger.LedgerPosting.Create(legs), default);
        await tx.CommitAsync();
    }

    /// <summary>A salaried customer with ~3 months of believable history (eligible for a microcredit).</summary>
    public async Task<Customer> RegisterSalariedAsync()
    {
        var c = await RegisterAsync();
        await PostBackdatedAsync(c.AccountId, true, 1_000_000m, 85, BfaNet.Domain.TransactionKind.Deposit, "Depósito inicial", "BFA");
        foreach (var d in new[] { 55, 28, 1 }) await PostBackdatedAsync(c.AccountId, true, 450_000m, d, BfaNet.Domain.TransactionKind.Deposit, "Salário", "Empresa Alfa");
        foreach (var d in new[] { 80, 74, 66, 58, 50, 44, 36, 30, 22, 15, 8, 3 })
            await PostBackdatedAsync(c.AccountId, false, 60_000m + d * 100, d, BfaNet.Domain.TransactionKind.ServicePayment, "Compras", "Kero Hipermercado", "11223/123456789");
        return c;
    }

    public static HttpRequestMessage Transfer(Customer from, string toIban, decimal amount, Guid? key = null, string? pin = null) =>
        new(HttpMethod.Post, "/api/v1/transfers")
        {
            Headers = { { "Idempotency-Key", (key ?? Guid.NewGuid()).ToString() } },
            Content = JsonContent.Create(new { fromAccountId = from.AccountId, toIban, amount, description = "Teste", pin = pin ?? from.Pin })
        };
}

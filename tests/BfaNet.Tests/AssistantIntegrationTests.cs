using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BfaNet.Tests;

public class AssistantIntegrationTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static async Task<JsonElement> Body(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(ApiFixture.Json);
    private static async Task<decimal> Balance(ApiFixture.Customer c) =>
        (await c.Client.GetFromJsonAsync<JsonElement>($"/api/v1/accounts/{c.AccountId}", ApiFixture.Json)).GetProperty("balance").GetDecimal();
    private static Task<HttpResponseMessage> Post(ApiFixture.Customer c, string url, object body, Guid? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return c.Client.SendAsync(req);
    }
    private static Task<HttpResponseMessage> Accept(ApiFixture.Customer c, decimal amount, int months, Guid? key = null, string? pin = null) =>
        Post(c, "/api/v1/assistant/credit/accept", new { accountId = c.AccountId, amount, months, pin = pin ?? c.Pin }, key);

    [DbFact]
    public async Task A_brand_new_customer_gets_a_friendly_placeholder_and_no_credit_offer()
    {
        var a = await api.RegisterAsync();
        var insights = await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/assistant/insights", ApiFixture.Json);
        Assert.False(insights.GetProperty("hasEnoughData").GetBoolean());
        Assert.Equal("welcome", insights.GetProperty("insights")[0].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, insights.GetProperty("saving").ValueKind);

        var offer = await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/assistant/credit", ApiFixture.Json);
        Assert.False(offer.GetProperty("eligible").GetBoolean());
        Assert.NotEmpty(offer.GetProperty("blockers").EnumerateArray());
        Assert.Equal(0m, offer.GetProperty("maxAmount").GetDecimal());
        Assert.Equal(HttpStatusCode.Conflict, (await Accept(a, 50_000m, 6)).StatusCode);
    }

    [DbFact]
    public async Task Insights_are_built_from_the_real_ledger_and_reflect_the_history()
    {
        var a = await api.RegisterSalariedAsync();
        var r = await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/assistant/insights", ApiFixture.Json);
        Assert.True(r.GetProperty("hasEnoughData").GetBoolean());
        Assert.Equal(450_000m, r.GetProperty("averageMonthlyIncome").GetDecimal());
        Assert.InRange(r.GetProperty("health").GetProperty("score").GetInt32(), 40, 100);
        Assert.NotEmpty(r.GetProperty("categories").EnumerateArray());
        Assert.Equal("Alimentacao", r.GetProperty("categories")[0].GetProperty("category").GetString());
        Assert.Equal(3, r.GetProperty("months").GetArrayLength());
        Assert.True(r.GetProperty("projectedSpend").GetDecimal() >= r.GetProperty("spentThisMonth").GetDecimal());

        // Nobody else's data leaks in: a second customer starts from zero.
        var b = await api.RegisterAsync();
        Assert.False((await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/assistant/insights", ApiFixture.Json)).GetProperty("hasEnoughData").GetBoolean());
    }

    [DbFact]
    public async Task Credit_offer_simulation_acceptance_and_repayment_move_real_money_correctly()
    {
        var a = await api.RegisterSalariedAsync();
        var offer = await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/assistant/credit", ApiFixture.Json);
        Assert.True(offer.GetProperty("eligible").GetBoolean(), string.Join(" | ", offer.GetProperty("blockers").EnumerateArray().Select(x => x.GetString())));
        var max = offer.GetProperty("maxAmount").GetDecimal();
        Assert.True(max >= 20_000m && max <= 1_000_000m);

        // The simulation is computed by the server and rejects anything outside the offer.
        var sim = await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/assistant/credit/simulate?amount=100000&months=6", ApiFixture.Json);
        Assert.Equal(sim.GetProperty("installment").GetDecimal() * 6, sim.GetProperty("totalRepayable").GetDecimal());
        Assert.Equal(100_000m - sim.GetProperty("fee").GetDecimal(), sim.GetProperty("netDisbursed").GetDecimal());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await a.Client.GetAsync($"/api/v1/assistant/credit/simulate?amount={max + 1000}&months=6")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await a.Client.GetAsync("/api/v1/assistant/credit/simulate?amount=100000&months=5")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await a.Client.GetAsync("/api/v1/assistant/credit/simulate?amount=100&months=6")).StatusCode);

        var before = await Balance(a);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Accept(a, 100_000m, 6, pin: "000111")).StatusCode);   // wrong PIN
        Assert.Equal(before, await Balance(a));

        var key = Guid.NewGuid();
        var ok = await Accept(a, 100_000m, 6, key);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var loan = await Body(ok);
        Assert.Equal(6, loan.GetProperty("installments").GetArrayLength());
        Assert.Equal("Active", loan.GetProperty("status").GetString());
        var fee = sim.GetProperty("fee").GetDecimal();
        Assert.Equal(before + 100_000m - fee, await Balance(a));

        // Replay with the same key: same loan, no second disbursement. Same key + other amount: rejected.
        var replay = await Body(await Accept(a, 100_000m, 6, key));
        Assert.Equal(loan.GetProperty("id").GetGuid(), replay.GetProperty("id").GetGuid());
        Assert.Equal(before + 100_000m - fee, await Balance(a));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Accept(a, 90_000m, 6, key)).StatusCode);

        // One active loan at a time.
        Assert.Equal(HttpStatusCode.Conflict, (await Accept(a, 50_000m, 6)).StatusCode);
        var offerNow = await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/assistant/credit", ApiFixture.Json);
        Assert.False(offerNow.GetProperty("eligible").GetBoolean());
        Assert.Equal(loan.GetProperty("id").GetGuid(), offerNow.GetProperty("activeLoan").GetProperty("id").GetGuid());

        // Repay every instalment.
        var loanId = loan.GetProperty("id").GetGuid();
        var installment = loan.GetProperty("installment").GetDecimal();
        for (var n = 1; n <= 6; n++)
        {
            var bal = await Balance(a);
            var repay = await Post(a, $"/api/v1/assistant/loans/{loanId}/repay", new { fromAccountId = a.AccountId, pin = a.Pin });
            Assert.Equal(HttpStatusCode.OK, repay.StatusCode);
            var receipt = await Body(repay);
            Assert.Equal("LoanRepayment", receipt.GetProperty("kind").GetString());
            Assert.Equal(installment, receipt.GetProperty("amount").GetDecimal());
            Assert.Equal(bal - installment, await Balance(a));
        }
        var loans = await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/assistant/loans", ApiFixture.Json);
        Assert.Equal("Paid", loans[0].GetProperty("status").GetString());
        Assert.Equal(0m, loans[0].GetProperty("outstanding").GetDecimal());
        Assert.Equal(HttpStatusCode.Conflict, (await Post(a, $"/api/v1/assistant/loans/{loanId}/repay", new { fromAccountId = a.AccountId, pin = a.Pin })).StatusCode);
    }

    [DbFact]
    public async Task Parallel_credit_requests_disburse_exactly_once()
    {
        var a = await api.RegisterSalariedAsync();
        var before = await Balance(a);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Accept(a, 60_000m, 6)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        var loans = await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/assistant/loans", ApiFixture.Json);
        Assert.Equal(1, loans.GetArrayLength());
        Assert.InRange(await Balance(a), before + 59_000m, before + 60_000m); // 60 000 − 1% fee
    }

    [DbFact]
    public async Task Other_customers_cannot_see_or_repay_someone_elses_loan()
    {
        var a = await api.RegisterSalariedAsync();
        var b = await api.RegisterSalariedAsync();
        var loan = await Body(await Accept(a, 50_000m, 6));
        var id = loan.GetProperty("id").GetGuid();
        Assert.Equal(0, (await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/assistant/loans", ApiFixture.Json)).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await Post(b, $"/api/v1/assistant/loans/{id}/repay", new { fromAccountId = b.AccountId, pin = b.Pin })).StatusCode);
    }

    [DbFact]
    public async Task The_ledger_stays_balanced_after_disbursement_and_repayment()
    {
        var a = await api.RegisterSalariedAsync();
        var loan = await Body(await Accept(a, 80_000m, 3));
        await Post(a, $"/api/v1/assistant/loans/{loan.GetProperty("id").GetGuid()}/repay", new { fromAccountId = a.AccountId, pin = a.Pin });

        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BfaNet.Infrastructure.Persistence.BankDbContext>();
        var debits = await db.LedgerEntries.Where(e => e.Direction == BfaNet.Domain.LedgerDirection.Debit).SumAsync(e => e.Amount);
        var credits = await db.LedgerEntries.Where(e => e.Direction == BfaNet.Domain.LedgerDirection.Credit).SumAsync(e => e.Amount);
        Assert.Equal(debits, credits);
        Assert.Equal(0m, await db.Accounts.SumAsync(x => x.Balance));
    }
}

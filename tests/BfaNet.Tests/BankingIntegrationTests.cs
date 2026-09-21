using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BfaNet.Tests;

public class BankingIntegrationTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static async Task<JsonElement> Body(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(ApiFixture.Json);
    private static async Task<decimal> Balance(ApiFixture.Customer c) =>
        (await c.Client.GetFromJsonAsync<JsonElement>($"/api/v1/accounts/{c.AccountId}", ApiFixture.Json)).GetProperty("balance").GetDecimal();

    [DbFact]
    public async Task Transfer_moves_money_and_replay_with_same_key_does_not_double_debit()
    {
        var a = await api.RegisterAsync(100_000);
        var b = await api.RegisterAsync();
        var key = Guid.NewGuid();

        var first = await a.Client.SendAsync(ApiFixture.Transfer(a, b.Iban, 25_000.50m, key));
        var replay = await a.Client.SendAsync(ApiFixture.Transfer(a, b.Iban, 25_000.50m, key));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal((await Body(first)).GetProperty("reference").GetString(), (await Body(replay)).GetProperty("reference").GetString());
        Assert.Equal(74_999.50m, await Balance(a));
        Assert.Equal(25_000.50m, await Balance(b));
    }

    [DbFact]
    public async Task Same_key_with_different_body_is_rejected()
    {
        var a = await api.RegisterAsync(50_000);
        var b = await api.RegisterAsync();
        var key = Guid.NewGuid();
        await a.Client.SendAsync(ApiFixture.Transfer(a, b.Iban, 100, key));
        var res = await a.Client.SendAsync(ApiFixture.Transfer(a, b.Iban, 999, key));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Equal("idempotency_mismatch", (await Body(res)).GetProperty("code").GetString());
    }

    [DbFact]
    public async Task Parallel_transfers_never_overdraw_and_ledger_stays_balanced()
    {
        var a = await api.RegisterAsync(1_000_000);
        var b = await api.RegisterAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            a.Client.SendAsync(ApiFixture.Transfer(a, b.Iban, 100_000))));

        var ok = results.Count(r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(10, ok);
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode));
        Assert.Equal(0m, await Balance(a));
        Assert.Equal(1_000_000m, await Balance(b));
    }

    [DbFact]
    public async Task Interbank_transfer_requires_beneficiary_name_and_charges_fee()
    {
        var a = await api.RegisterAsync(100_000);
        var noName = await a.Client.SendAsync(ApiFixture.Transfer(a, OtherBankIban(), 10_000));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noName.StatusCode);

        var withName = ApiFixture.Transfer(a, OtherBankIban(), 10_000);
        withName.Content = JsonContent.Create(new { fromAccountId = a.AccountId, toIban = OtherBankIban(), beneficiaryName = "Ana Costa", amount = 10_000, pin = a.Pin });
        var ok = await a.Client.SendAsync(withName);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(150m, (await Body(ok)).GetProperty("fee").GetDecimal());
        Assert.Equal(89_850m, await Balance(a));
    }

    [DbFact]
    public async Task Customers_cannot_touch_each_others_accounts()
    {
        var a = await api.RegisterAsync(10_000);
        var b = await api.RegisterAsync(10_000);

        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/v1/accounts/{a.AccountId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/v1/accounts/{a.AccountId}/statement")).StatusCode);
        var steal = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers")
        {
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
            Content = JsonContent.Create(new { fromAccountId = a.AccountId, toIban = b.Iban, amount = 5_000, pin = b.Pin })
        };
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.SendAsync(steal)).StatusCode);
        Assert.Equal(10_000m, await Balance(a));
    }

    [DbFact]
    public async Task Wrong_pin_locks_transfers_after_three_attempts()
    {
        var a = await api.RegisterAsync(10_000);
        var b = await api.RegisterAsync();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await a.Client.SendAsync(ApiFixture.Transfer(a, b.Iban, 10, pin: "000111"))).StatusCode);

        var locked = await a.Client.SendAsync(ApiFixture.Transfer(a, b.Iban, 10)); // even the right PIN is refused now
        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
        Assert.Equal(10_000m, await Balance(a));
    }

    [DbFact]
    public async Task Login_locks_after_five_bad_passwords()
    {
        var a = await api.RegisterAsync();
        var anon = api.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/login", new { customerNumber = a.Number, password = "Errada#12345" })).StatusCode);
        var res = await anon.PostAsJsonAsync("/api/v1/auth/login", new { customerNumber = a.Number, password = a.Password });
        Assert.Equal((HttpStatusCode)423, res.StatusCode);
    }

    [DbFact]
    public async Task Refresh_tokens_rotate_and_reuse_revokes_the_session()
    {
        var a = await api.RegisterAsync();
        var anon = api.CreateClient();
        anon.DefaultRequestHeaders.Add("X-BFA-Client", "mobile");

        var r1 = await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = a.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var next = (await Body(r1)).GetProperty("refreshToken").GetString();
        Assert.NotEqual(a.RefreshToken, next);

        await Task.Delay(TimeSpan.FromSeconds(11)); // beyond the double-tab grace window
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = a.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = next })).StatusCode);
    }

    [DbFact]
    public async Task PII_is_encrypted_at_rest()
    {
        var a = await api.RegisterAsync();
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BfaNet.Infrastructure.Persistence.BankDbContext>();
        var raw = await db.Database.SqlQuery<string>($"SELECT email AS \"Value\" FROM customers WHERE customer_number = {a.Number}").SingleAsync();
        Assert.StartsWith("v1.k1.", raw);
        Assert.DoesNotContain("@", raw);
    }

    [DbFact]
    public async Task Cookie_authenticated_writes_require_the_csrf_header()
    {
        var a = await api.RegisterAsync();
        var web = api.CreateClient(new() { HandleCookies = true });
        web.DefaultRequestHeaders.Add("X-BFA-Client", "web");
        var login = await web.PostAsJsonAsync("/api/v1/auth/login", new { customerNumber = a.Number, password = a.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Null((await Body(login)).GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await web.GetAsync("/api/v1/me")).StatusCode); // cookie auth works for reads

        // A cross-site form post carries the cookies but cannot add the custom header.
        web.DefaultRequestHeaders.Remove("X-BFA-Client");
        var forged = await web.PostAsJsonAsync("/api/v1/auth/change-pin", new { currentPin = a.Pin, newPin = "582917" });
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
    }

    private static string OtherBankIban()
    {
        // Valid AO IBAN for another bank code (0040): compute check digits with the same mod-97 scheme.
        var first19 = "0040" + "0001" + "00000000012";
        var nib = 98 - (int)(System.Numerics.BigInteger.Parse(first19 + "00") % 97);
        var bban = first19 + nib.ToString("D2");
        var digits = string.Concat((bban + "AO00").Select(c => char.IsLetter(c) ? (c - 'A' + 10).ToString() : c.ToString()));
        return $"AO{98 - (int)(System.Numerics.BigInteger.Parse(digits) % 97):D2}{bban}";
    }
}

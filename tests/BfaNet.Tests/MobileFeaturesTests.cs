using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SkiaSharp;
using Xunit;

namespace BfaNet.Tests;

public class MobileFeaturesTests(ApiFixture api) : IClassFixture<ApiFixture>
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

    // ---------- recharges ----------

    [DbFact]
    public async Task Recharges_work_for_every_provider_with_provider_specific_identifiers()
    {
        var a = await api.RegisterAsync(2_000_000);
        var cases = new (string provider, string id, decimal amount)[]
        { ("Unitel", "+244 923 456 789", 1_000), ("Africell", "955123456", 500), ("Dstv", "1234567890", 15_000), ("Zap", "9876543210", 8_000), ("Ende", "123456789012", 20_000) };

        var spent = 0m;
        foreach (var (provider, id, amount) in cases)
        {
            var res = await Post(a, "/api/v1/payments/recharges", new { fromAccountId = a.AccountId, provider, identifier = id, amount, pin = a.Pin });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var receipt = await Body(res);
            Assert.Equal("TopUp", receipt.GetProperty("kind").GetString());
            spent += amount;
        }
        Assert.Equal(2_000_000m - spent, await Balance(a));
    }

    [DbFact]
    public async Task Recharge_rejects_bad_identifiers_and_amount_ranges()
    {
        var a = await api.RegisterAsync(100_000);
        async Task<HttpStatusCode> Try(string provider, string id, decimal amount) =>
            (await Post(a, "/api/v1/payments/recharges", new { fromAccountId = a.AccountId, provider, identifier = id, amount, pin = a.Pin })).StatusCode;

        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try("Unitel", "12345", 500));      // not a mobile number
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try("Ende", "12345", 500));        // meter too short
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try("Dstv", "12345678901234", 500)); // too long
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try("Unitel", "923456789", 60_000)); // mobile cap 50 000
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await Try("Unitel", "923456789", 50));    // below minimum
        Assert.Equal(100_000m, await Balance(a));
    }

    // ---------- state payments ----------

    [DbFact]
    public async Task State_payment_requires_a_13_digit_reference()
    {
        var a = await api.RegisterAsync(100_000);
        var ok = await Post(a, "/api/v1/payments/state", new { fromAccountId = a.AccountId, reference = "1234567890123", amount = 12_500, pin = a.Pin });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("StatePayment", (await Body(ok)).GetProperty("kind").GetString());

        foreach (var bad in new[] { "123456789012", "12345678901234", "12345678901ab" })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(a, "/api/v1/payments/state", new { fromAccountId = a.AccountId, reference = bad, amount = 100, pin = a.Pin })).StatusCode);
        Assert.Equal(87_500m, await Balance(a));
    }

    // ---------- KWiK ----------

    [DbFact]
    public async Task Kwik_resolves_masked_name_and_moves_money_by_phone_key()
    {
        var a = await api.RegisterAsync(50_000);
        var b = await api.RegisterAsync();

        var resolve = await Post(a, "/api/v1/transfers/kwik/resolve", new { key = b.Phone });
        var r = await Body(resolve);
        Assert.True(r.GetProperty("found").GetBoolean());
        Assert.Contains("*", r.GetProperty("holderMasked").GetString());

        var pay = await Post(a, "/api/v1/transfers/kwik", new { fromAccountId = a.AccountId, key = $"+244 {b.Phone}", amount = 7_500, description = "Almoço", pin = a.Pin });
        Assert.Equal(HttpStatusCode.OK, pay.StatusCode);
        Assert.Equal(42_500m, await Balance(a));
        Assert.Equal(7_500m, await Balance(b));
    }

    [DbFact]
    public async Task Kwik_rejects_unknown_keys_and_self_transfers()
    {
        var a = await api.RegisterAsync(10_000);
        var unknown = await Post(a, "/api/v1/transfers/kwik", new { fromAccountId = a.AccountId, key = "900000000", amount = 100, pin = a.Pin });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        var self = await Post(a, "/api/v1/transfers/kwik", new { fromAccountId = a.AccountId, key = a.Phone, amount = 100, pin = a.Pin });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
        Assert.False((await Body(await Post(a, "/api/v1/transfers/kwik/resolve", new { key = "900000000" }))).GetProperty("found").GetBoolean());
        Assert.Equal(10_000m, await Balance(a));
    }

    // ---------- avatar (server-side processing) ----------

    /// <summary>A real, decodable image (diagonal gradient) — the server re-encodes it, so fake bytes no longer pass.</summary>
    private static byte[] MakeImage(int w, int h, SKEncodedImageFormat format = SKEncodedImageFormat.Png, bool transparent = false)
    {
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, transparent ? SKAlphaType.Premul : SKAlphaType.Opaque));
        surface.Canvas.Clear(transparent ? SKColors.Transparent : SKColors.White);
        if (!transparent)
            using (var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h), [SKColors.OrangeRed, SKColors.Navy], SKShaderTileMode.Clamp) })
                surface.Canvas.DrawRect(0, 0, w, h, paint);
        using var data = surface.Snapshot().Encode(format, 90);
        return data.ToArray();
    }

    private static Task<HttpResponseMessage> PutAvatar(ApiFixture.Customer c, byte[] bytes, string field = "file", string fileName = "foto.png", string mime = "image/png")
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(mime); // the server never trusts this
        form.Add(part, field, fileName);
        return c.Client.PutAsync("/api/v1/me/avatar", form);
    }

    [DbFact]
    public async Task Avatar_is_cropped_resized_and_reencoded_on_the_server_and_private_to_its_owner()
    {
        var a = await api.RegisterAsync();
        var b = await api.RegisterAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync("/api/v1/me/avatar")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/me", ApiFixture.Json)).GetProperty("avatarVersion").ValueKind);

        // A 1600x900 landscape PNG goes in, a 512x512 JPEG comes out.
        Assert.Equal(HttpStatusCode.NoContent, (await PutAvatar(a, MakeImage(1600, 900))).StatusCode);
        var got = await a.Client.GetAsync("/api/v1/me/avatar");
        Assert.Equal("image/jpeg", got.Content.Headers.ContentType!.MediaType);
        var stored = await got.Content.ReadAsByteArrayAsync();
        Assert.InRange(stored.Length, 1, 300 * 1024);
        using (var bmp = SKBitmap.Decode(stored)) { Assert.Equal(512, bmp.Width); Assert.Equal(512, bmp.Height); }
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, stored[..3]);
        Assert.Equal(JsonValueKind.Number, (await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/me", ApiFixture.Json)).GetProperty("avatarVersion").ValueKind);

        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync("/api/v1/me/avatar")).StatusCode); // no way to read someone else's
        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.DeleteAsync("/api/v1/me/avatar")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync("/api/v1/me/avatar")).StatusCode);
    }

    [DbFact]
    public async Task Avatar_accepts_jpeg_and_webp_and_flattens_transparency_onto_white()
    {
        var a = await api.RegisterAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await PutAvatar(a, MakeImage(800, 800, SKEncodedImageFormat.Jpeg), fileName: "f.jpg", mime: "image/jpeg")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAvatar(a, MakeImage(700, 900, SKEncodedImageFormat.Webp), fileName: "f.webp", mime: "image/webp")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await PutAvatar(a, MakeImage(600, 600, SKEncodedImageFormat.Png, transparent: true))).StatusCode);
        using var bmp = SKBitmap.Decode(await (await a.Client.GetAsync("/api/v1/me/avatar")).Content.ReadAsByteArrayAsync());
        var px = bmp.GetPixel(256, 256);
        Assert.True(px.Red > 240 && px.Green > 240 && px.Blue > 240, $"expected white, got {px}");
    }

    [DbFact]
    public async Task Avatar_rejects_fake_images_bombs_oversize_and_malformed_requests()
    {
        var a = await api.RegisterAsync();
        // Valid JPEG magic bytes followed by junk: passes the sniff, fails decoding.
        var fake = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.Concat(System.Text.Encoding.UTF8.GetBytes("<script>alert(1)</script>")).ToArray();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutAvatar(a, fake, fileName: "x.jpg", mime: "image/jpeg")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutAvatar(a, System.Text.Encoding.UTF8.GetBytes("<html></html>"), fileName: "x.jpg", mime: "image/jpeg")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutAvatar(a, [])).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutAvatar(a, MakeImage(9000, 20))).StatusCode);          // side > 8000 px
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PutAvatar(a, MakeImage(700, 700), field: "other")).StatusCode); // wrong field name

        var big = new byte[11 * 1024 * 1024]; big[0] = 0xFF; big[1] = 0xD8; big[2] = 0xFF;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PutAvatar(a, big)).StatusCode);

        // Raw body (the old API) is no longer accepted.
        var raw = new ByteArrayContent(MakeImage(300, 300)); raw.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await a.Client.PutAsync("/api/v1/me/avatar", raw)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync("/api/v1/me/avatar")).StatusCode);
    }

    // ---------- PIN verification (used to arm biometric payments) ----------

    [DbFact]
    public async Task Verify_pin_accepts_the_right_pin_and_locks_after_three_wrong_ones()
    {
        var a = await api.RegisterAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.PostAsJsonAsync("/api/v1/auth/verify-pin", new { pin = a.Pin })).StatusCode);
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await a.Client.PostAsJsonAsync("/api/v1/auth/verify-pin", new { pin = "000111" })).StatusCode);
        Assert.Equal((HttpStatusCode)423, (await a.Client.PostAsJsonAsync("/api/v1/auth/verify-pin", new { pin = a.Pin })).StatusCode);
    }

    // ---------- biometric login ----------

    [DbFact]
    public async Task Biometric_enrolment_needs_the_password_and_the_device_token_logs_in()
    {
        var a = await api.RegisterAsync();
        var anon = api.CreateClient();
        anon.DefaultRequestHeaders.Add("X-BFA-Client", "mobile");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await a.Client.PostAsJsonAsync("/api/v1/auth/biometric/enroll", new { password = "Errada#12345", deviceLabel = "iPhone" })).StatusCode);

        var enrol = await a.Client.PostAsJsonAsync("/api/v1/auth/biometric/enroll", new { password = a.Password, deviceLabel = "iPhone de Teste" });
        Assert.Equal(HttpStatusCode.OK, enrol.StatusCode);
        var token = (await Body(enrol)).GetProperty("deviceToken").GetString()!;

        var login = await anon.PostAsJsonAsync("/api/v1/auth/biometric/login", new { customerNumber = a.Number, deviceToken = token });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False(string.IsNullOrEmpty((await Body(login)).GetProperty("accessToken").GetString()));

        // wrong token, right user; right token, wrong user
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/biometric/login", new { customerNumber = a.Number, deviceToken = "nope" })).StatusCode);
        var other = await api.RegisterAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/biometric/login", new { customerNumber = other.Number, deviceToken = token })).StatusCode);
    }

    [DbFact]
    public async Task Biometric_token_dies_on_disable_and_on_password_change()
    {
        var a = await api.RegisterAsync();
        var anon = api.CreateClient();
        anon.DefaultRequestHeaders.Add("X-BFA-Client", "mobile");
        async Task<string> Enrol() => (await Body(await a.Client.PostAsJsonAsync("/api/v1/auth/biometric/enroll", new { password = a.Password }))).GetProperty("deviceToken").GetString()!;
        Task<HttpResponseMessage> Login(string t) => anon.PostAsJsonAsync("/api/v1/auth/biometric/login", new { customerNumber = a.Number, deviceToken = t });

        var t1 = await Enrol();
        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.PostAsJsonAsync("/api/v1/auth/biometric/disable", new { deviceToken = t1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(t1)).StatusCode);

        var t2 = await Enrol();
        Assert.Equal(HttpStatusCode.OK, (await Login(t2)).StatusCode);
        var change = await a.Client.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = a.Password, newPassword = "Nova#Cofre-2027y" });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(t2)).StatusCode); // password change revoked every enrolment
    }

    // ---------- receipt PDF (issued by the backend) ----------

    [DbFact]
    public async Task Receipt_pdf_is_issued_by_the_backend_for_payer_and_payee_but_not_for_strangers()
    {
        var a = await api.RegisterAsync(80_000);
        var b = await api.RegisterAsync();
        var stranger = await api.RegisterAsync();

        var receipt = await Body(await a.Client.SendAsync(ApiFixture.Transfer(a, b.Iban, 12_345.67m)));
        var id = receipt.GetProperty("transactionId").GetGuid();
        var reference = receipt.GetProperty("reference").GetString()!;

        var payerPdf = await a.Client.GetAsync($"/api/v1/transactions/{id}/receipt.pdf");
        Assert.Equal(HttpStatusCode.OK, payerPdf.StatusCode);
        Assert.Equal("application/pdf", payerPdf.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"comprovativo-{reference}.pdf", payerPdf.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var payerBytes = await payerPdf.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(payerBytes, 0, 5));
        Assert.InRange(payerBytes.Length, 3_000, 400_000);

        var payeePdf = await b.Client.GetAsync($"/api/v1/transactions/{id}/receipt.pdf");
        Assert.Equal(HttpStatusCode.OK, payeePdf.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.GetAsync($"/api/v1/transactions/{id}/receipt.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync($"/api/v1/transactions/{Guid.NewGuid()}/receipt.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync($"/api/v1/transactions/{id}/receipt.pdf")).StatusCode);

        // Optional: dump the PDFs so a human (or pdftotext) can inspect them.
        if (Environment.GetEnvironmentVariable("BFANET_TEST_PDF_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, "payer.pdf"), payerBytes);
            await File.WriteAllBytesAsync(Path.Combine(dir, "payee.pdf"), await payeePdf.Content.ReadAsByteArrayAsync());
            await File.WriteAllTextAsync(Path.Combine(dir, "meta.txt"), $"payerNumber={a.Number}\npayerPhone={a.Phone}\npayerIban={a.Iban}\npayeeIban={b.Iban}\nref={reference}\n");
        }
    }

    // ---------- statement PDF (issued by the backend) ----------

    [DbFact]
    public async Task Statement_pdf_is_generated_by_the_backend_for_the_owner_only_and_validates_the_period()
    {
        var a = await api.RegisterAsync(500_000);
        var b = await api.RegisterAsync();
        for (var i = 1; i <= 25; i++) await a.Client.SendAsync(ApiFixture.Transfer(a, b.Iban, 1_000m + i));

        var res = await a.Client.GetAsync($"/api/v1/accounts/{a.AccountId}/statement.pdf");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("extracto-", res.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.InRange(bytes.Length, 5_000, 800_000);

        // someone else's account / unknown account → the same 404
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/v1/accounts/{a.AccountId}/statement.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync($"/api/v1/accounts/{Guid.NewGuid()}/statement.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync($"/api/v1/accounts/{a.AccountId}/statement.pdf")).StatusCode);

        // period validation
        var url = $"/api/v1/accounts/{a.AccountId}/statement.pdf";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await a.Client.GetAsync($"{url}?from=2026-02-10&to=2026-02-01")).StatusCode);   // from > to
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await a.Client.GetAsync($"{url}?from=2020-01-01&to=2026-01-01")).StatusCode);   // > 366 days
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await a.Client.GetAsync($"{url}?from=2026-01-01&to=2999-01-01")).StatusCode);   // future
        Assert.Equal(HttpStatusCode.OK, (await a.Client.GetAsync($"{url}?from=2025-01-01&to=2025-01-31")).StatusCode);                    // empty period is fine

        if (Environment.GetEnvironmentVariable("BFANET_TEST_PDF_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, "statement.pdf"), bytes);
        }
    }

    // ---------- cards & public info ----------

    [DbFact]
    public async Task Card_settings_include_atm_and_international_and_persist()
    {
        var a = await api.RegisterAsync();
        var card = (await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/cards", ApiFixture.Json))[0];
        Assert.True(card.GetProperty("atmWithdrawals").GetBoolean());
        Assert.False(card.GetProperty("internationalPayments").GetBoolean()); // off by default

        var res = await a.Client.PatchAsJsonAsync($"/api/v1/cards/{card.GetProperty("id").GetGuid()}", new { atmWithdrawals = false, internationalPayments = true, dailyLimit = 150000 });
        var updated = await Body(res);
        Assert.False(updated.GetProperty("atmWithdrawals").GetBoolean());
        Assert.True(updated.GetProperty("internationalPayments").GetBoolean());
        Assert.Equal(150000m, updated.GetProperty("dailyLimit").GetDecimal());
    }

    [DbFact]
    public async Task Public_contacts_and_about_are_available_without_login()
    {
        var anon = api.CreateClient();
        var contacts = await anon.GetFromJsonAsync<JsonElement>("/api/v1/public/contacts", ApiFixture.Json);
        Assert.Contains(contacts.EnumerateArray(), c => c.GetProperty("url").GetString() == "tel:923120120");
        var about = await anon.GetFromJsonAsync<JsonElement>("/api/v1/public/about", ApiFixture.Json);
        Assert.StartsWith("https://www.bfa.ao/", about.GetProperty("url").GetString());
    }
}

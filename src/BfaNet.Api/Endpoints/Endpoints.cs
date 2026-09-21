using BfaNet.Api.Http;
using BfaNet.Application;
using BfaNet.Application.Abstractions;
using BfaNet.Application.Common;
using BfaNet.Application.Contracts;
using FluentValidation;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BfaNet.Api.Endpoints;

public static class ApiEndpoints
{
    public static void MapBankApi(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1");
        MapPublic(v1.MapGroup("/public"));
        MapAuth(v1.MapGroup("/auth"));

        var secured = v1.MapGroup("").RequireAuthorization();
        MapProfile(secured);
        MapAccounts(secured.MapGroup("/accounts"));
        MapMoney(secured);
        MapBeneficiaries(secured.MapGroup("/beneficiaries"));
        MapCards(secured.MapGroup("/cards"));
    }

    private static void MapPublic(RouteGroupBuilder g)
    {
        g.MapGet("/exchange-rates", (IPublicInfoService s, CancellationToken ct) => s.ExchangeRatesAsync(ct));
        g.MapGet("/products", (IPublicInfoService s) => s.Products());
        g.MapGet("/contacts", (IPublicInfoService s) => s.Contacts());
        g.MapGet("/about", (IPublicInfoService s) => s.About());
    }

    private static void MapAuth(RouteGroupBuilder g)
    {
        g.MapPost("/register", async (RegisterRequest r, IAuthService auth, IRequestContext ctx, IOptions<SecurityOptions> o, HttpContext http, CancellationToken ct) =>
            Results.Json(AuthCookies.Deliver(http, o.Value, await auth.RegisterAsync(r, ct), ctx.IsWebClient), statusCode: 201))
            .Validate<RegisterRequest>().RequireRateLimiting(RateLimiting.Auth);

        g.MapPost("/login", async (LoginRequest r, IAuthService auth, IRequestContext ctx, IOptions<SecurityOptions> o, HttpContext http, CancellationToken ct) =>
            Results.Ok(AuthCookies.Deliver(http, o.Value, await auth.LoginAsync(r, ct), ctx.IsWebClient)))
            .Validate<LoginRequest>().RequireRateLimiting(RateLimiting.Auth);

        // Body is optional: web relies on the HttpOnly cookie, mobile posts its token.
        g.MapPost("/refresh", async ([FromBody] RefreshRequest? body, IAuthService auth, IRequestContext ctx, IOptions<SecurityOptions> o, HttpContext http, CancellationToken ct) =>
        {
            var token = ctx.IsWebClient ? http.Request.Cookies[o.Value.RefreshCookieName] : body?.RefreshToken;
            try { return Results.Ok(AuthCookies.Deliver(http, o.Value, await auth.RefreshAsync(token, ct), ctx.IsWebClient)); }
            catch (AppException) when (ctx.IsWebClient) { AuthCookies.Clear(http, o.Value); throw; }
        }).RequireRateLimiting(RateLimiting.Auth);

        g.MapPost("/logout", async ([FromBody] RefreshRequest? body, IAuthService auth, IRequestContext ctx, IOptions<SecurityOptions> o, HttpContext http, CancellationToken ct) =>
        {
            await auth.LogoutAsync(ctx.IsWebClient ? http.Request.Cookies[o.Value.RefreshCookieName] : body?.RefreshToken, ct);
            AuthCookies.Clear(http, o.Value);
            return Results.NoContent();
        });

        // Biometric login: a per-device secret released by the OS only after Face ID / fingerprint on the phone.
        g.MapPost("/biometric/login", async (BiometricLoginRequest r, IAuthService auth, IRequestContext ctx, IOptions<SecurityOptions> o, HttpContext http, CancellationToken ct) =>
            Results.Ok(AuthCookies.Deliver(http, o.Value, await auth.BiometricLoginAsync(r, ct), ctx.IsWebClient)))
            .Validate<BiometricLoginRequest>().RequireRateLimiting(RateLimiting.Auth);

        var authed = g.MapGroup("").RequireAuthorization();
        authed.MapPost("/biometric/enroll", (BiometricEnrollRequest r, IAuthService auth, CancellationToken ct) => auth.EnrollBiometricAsync(r, ct))
            .Validate<BiometricEnrollRequest>().RequireRateLimiting(RateLimiting.Auth);
        authed.MapPost("/verify-pin", async (VerifyPinRequest r, IAuthService auth, CancellationToken ct) =>
        { await auth.VerifyPinAsync(r, ct); return Results.NoContent(); }).Validate<VerifyPinRequest>().RequireRateLimiting(RateLimiting.Auth);
        authed.MapPost("/biometric/disable", async (BiometricDisableRequest r, IAuthService auth, CancellationToken ct) =>
        { await auth.DisableBiometricAsync(r, ct); return Results.NoContent(); }).Validate<BiometricDisableRequest>();
        authed.MapPost("/logout-all", async (IAuthService auth, IOptions<SecurityOptions> o, HttpContext http, CancellationToken ct) =>
        {
            await auth.LogoutAllAsync(ct);
            AuthCookies.Clear(http, o.Value);
            return Results.NoContent();
        });
        authed.MapPost("/change-password", async (ChangePasswordRequest r, IAuthService auth, CancellationToken ct) =>
        { await auth.ChangePasswordAsync(r, ct); return Results.NoContent(); })
            .Validate<ChangePasswordRequest>().RequireRateLimiting(RateLimiting.Auth);
        authed.MapPost("/change-pin", async (ChangePinRequest r, IAuthService auth, CancellationToken ct) =>
        { await auth.ChangePinAsync(r, ct); return Results.NoContent(); })
            .Validate<ChangePinRequest>().RequireRateLimiting(RateLimiting.Auth);
        authed.MapGet("/sessions", (IAuthService auth, CancellationToken ct) => auth.ListSessionsAsync(ct));
        authed.MapDelete("/sessions/{familyId:guid}", async (Guid familyId, IAuthService auth, CancellationToken ct) =>
        { await auth.RevokeSessionAsync(familyId, ct); return Results.NoContent(); });
    }

    private static void MapProfile(RouteGroupBuilder g)
    {
        g.MapGet("/me", (IAuthService auth, CancellationToken ct) => auth.GetProfileAsync(ct));

        g.MapGet("/me/avatar", async (IAvatarService svc, HttpContext http, CancellationToken ct) =>
        {
            var file = await svc.GetAsync(ct);
            if (file is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=86400"; // clients bust the cache with ?v=<avatarVersion>
            return Results.File(file.Data, file.ContentType);
        });

        // multipart/form-data with one "file" part. The server validates, crops, resizes and re-encodes it, so
        // clients upload the original as-is (no client-side image processing or Blob juggling).
        g.MapPut("/me/avatar", async (HttpContext http, IAvatarService svc, CancellationToken ct) =>
        {
            const int max = IAvatarService.MaxUploadBytes;
            // Kestrel enforces a 32 KB default; raise it for this endpoint only. (Absent/read-only under TestServer.)
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = max + 64 * 1024;
            if (http.Request.ContentLength is > max + 64 * 1024) throw new AppException(ErrorCodes.Validation, "A imagem excede 10 MB.", 413);
            if (!http.Request.HasFormContentType) throw new AppException(ErrorCodes.Validation, "Envie a imagem como multipart/form-data (campo \"file\").", 415);

            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.Count == 1 ? form.Files[0] : null;
            if (file is null || file.Name != "file") throw AppException.Invalid("file", "Envie exactamente um ficheiro no campo \"file\".");
            if (file.Length > max) throw new AppException(ErrorCodes.Validation, "A imagem excede 10 MB.", 413);

            using var ms = new MemoryStream((int)file.Length);
            await file.CopyToAsync(ms, ct);
            await svc.SaveAsync(ms.ToArray(), ct);
            return Results.NoContent();
        }).RequireRateLimiting(RateLimiting.Money);

        g.MapDelete("/me/avatar", async (IAvatarService svc, CancellationToken ct) => { await svc.DeleteAsync(ct); return Results.NoContent(); });
    }

    private static void MapAccounts(RouteGroupBuilder g)
    {
        g.MapGet("", (IAccountService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapGet("/{id:guid}", (Guid id, IAccountService s, CancellationToken ct) => s.GetAsync(id, ct));
        g.MapPatch("/{id:guid}", (Guid id, RenameAccountRequest r, IAccountService s, CancellationToken ct) => s.RenameAsync(id, r, ct))
            .Validate<RenameAccountRequest>();
        // Extracto issued by the bank as a PDF (logo, holder, account, period, balances, movements).
        g.MapGet("/{id:guid}/statement.pdf", async (Guid id, DateOnly? from, DateOnly? to, IStatementPdfService pdf, CancellationToken ct) =>
        {
            var doc = await pdf.BuildAsync(id, from, to, ct);
            return Results.File(doc.Content, "application/pdf", doc.FileName);
        }).RequireRateLimiting(RateLimiting.Money);

        g.MapGet("/{id:guid}/statement", async (Guid id, [AsParameters] StatementQuery q, IAccountService s, IValidator<StatementQuery> v, CancellationToken ct) =>
        {
            await v.ValidateAndThrowAsync(q, ct);
            return Results.Ok(await s.StatementAsync(id, q, ct));
        });
    }

    private static void MapMoney(RouteGroupBuilder g)
    {
        g.MapPost("/transfers/resolve-iban", (ResolveIbanRequest r, ITransferService s, CancellationToken ct) => s.ResolveIbanAsync(r, ct))
            .Validate<ResolveIbanRequest>().RequireRateLimiting(RateLimiting.Money);

        g.MapPost("/transfers", async (TransferRequest r, HttpContext http, ITransferService s, CancellationToken ct) =>
            Results.Ok(await s.TransferAsync(IdempotencyKey(http), r, ct))).Validate<TransferRequest>().RequireRateLimiting(RateLimiting.Money);

        g.MapPost("/payments/services", async (ServicePaymentRequest r, HttpContext http, ITransferService s, CancellationToken ct) =>
            Results.Ok(await s.PayServiceAsync(IdempotencyKey(http), r, ct))).Validate<ServicePaymentRequest>().RequireRateLimiting(RateLimiting.Money);

        g.MapPost("/payments/recharges", async (RechargeRequest r, HttpContext http, ITransferService s, CancellationToken ct) =>
            Results.Ok(await s.RechargeAsync(IdempotencyKey(http), r, ct))).Validate<RechargeRequest>().RequireRateLimiting(RateLimiting.Money);

        g.MapPost("/payments/state", async (StatePaymentRequest r, HttpContext http, ITransferService s, CancellationToken ct) =>
            Results.Ok(await s.PayStateAsync(IdempotencyKey(http), r, ct))).Validate<StatePaymentRequest>().RequireRateLimiting(RateLimiting.Money);

        g.MapPost("/transfers/kwik/resolve", (KwikResolveRequest r, ITransferService s, CancellationToken ct) => s.ResolveKwikAsync(r, ct))
            .Validate<KwikResolveRequest>().RequireRateLimiting(RateLimiting.Money);

        g.MapPost("/transfers/kwik", async (KwikTransferRequest r, HttpContext http, ITransferService s, CancellationToken ct) =>
            Results.Ok(await s.KwikTransferAsync(IdempotencyKey(http), r, ct))).Validate<KwikTransferRequest>().RequireRateLimiting(RateLimiting.Money);

        g.MapGet("/transactions/{id:guid}", (Guid id, ITransferService s, CancellationToken ct) => s.GetReceiptAsync(id, ct));

        // Comprovativo generated by the bank (not by the client): logo, payer and account details, reference.
        g.MapGet("/transactions/{id:guid}/receipt.pdf", async (Guid id, IReceiptPdfService pdf, CancellationToken ct) =>
        {
            var doc = await pdf.BuildAsync(id, ct);
            return Results.File(doc.Content, "application/pdf", doc.FileName);
        }).RequireRateLimiting(RateLimiting.Money);
    }

    private static void MapBeneficiaries(RouteGroupBuilder g)
    {
        g.MapGet("", (IBeneficiaryService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapPost("", async (BeneficiaryRequest r, IBeneficiaryService s, CancellationToken ct) =>
            Results.Json(await s.CreateAsync(r, ct), statusCode: 201)).Validate<BeneficiaryRequest>();
        g.MapDelete("/{id:guid}", async (Guid id, IBeneficiaryService s, CancellationToken ct) =>
        { await s.DeleteAsync(id, ct); return Results.NoContent(); });
    }

    private static void MapCards(RouteGroupBuilder g)
    {
        g.MapGet("", (ICardService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapPatch("/{id:guid}", (Guid id, UpdateCardRequest r, ICardService s, CancellationToken ct) => s.UpdateAsync(id, r, ct))
            .Validate<UpdateCardRequest>();
    }

    private static Guid IdempotencyKey(HttpContext http) =>
        Guid.TryParse(http.Request.Headers["Idempotency-Key"].ToString(), out var key) && key != Guid.Empty
            ? key
            : throw new AppException(ErrorCodes.Validation, "Cabeçalho Idempotency-Key (UUID) obrigatório.", 400);
}

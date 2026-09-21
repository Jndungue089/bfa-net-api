using System.Net;
using System.Text.Json.Serialization;
using BfaNet.Api.Endpoints;
using BfaNet.Api.Http;
using BfaNet.Application;
using BfaNet.Application.Abstractions;
using BfaNet.Infrastructure;
using BfaNet.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
// Personal, git-ignored overrides (e.g. local DB password). Development only; wins over appsettings.Development.json.
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 32 * 1024; // JSON payloads here are tiny; reject anything bigger.
});

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddValidatorsFromAssemblyContaining<SecurityOptions>(includeInternalTypes: true);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IRequestContext, RequestContext>();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddApiRateLimiting(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow; // no mass-assignment via extra fields
});
builder.Services.AddOpenApi();

var security = builder.Configuration.GetSection(SecurityOptions.Section).Get<SecurityOptions>() ?? new();
if (!builder.Environment.IsDevelopment() && !security.SecureCookies)
    throw new InvalidOperationException("Security:SecureCookies não pode ser false fora de desenvolvimento.");
if (Convert.FromBase64String(security.JwtSigningKey.Length == 0 ? "AA==" : security.JwtSigningKey).Length < 32)
    throw new InvalidOperationException("Security:JwtSigningKey deve ter pelo menos 32 bytes (base64).");

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Only the Next.js server (same host / private network) may set these; the list is explicit.
    o.KnownNetworks.Clear(); o.KnownProxies.Clear();
    foreach (var ip in builder.Configuration.GetSection("TrustedProxies").Get<string[]>() ?? ["127.0.0.1", "::1"])
        o.KnownProxies.Add(IPAddress.Parse(ip));
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = security.JwtIssuer,
        ValidateAudience = true, ValidAudience = security.JwtAudience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(security.JwtSigningKey)),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256], // blocks alg=none / algorithm confusion
        RequireExpirationTime = true, ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(20),
        NameClaimType = "sub"
    };
    o.Events = new JwtBearerEvents
    {
        // Browsers authenticate with the HttpOnly cookie; native apps send Authorization: Bearer.
        OnMessageReceived = ctx =>
        {
            if (string.IsNullOrEmpty(ctx.Token) && !ctx.Request.Headers.ContainsKey("Authorization"))
                ctx.Token = ctx.Request.Cookies[security.AccessCookieName];
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization();

var app = builder.Build();

// Fail fast if key material is missing/short (resolving constructs and validates them).
app.Services.GetRequiredService<IFieldEncryptor>();
app.Services.GetRequiredService<IBlindIndex>();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseApiSecurityHeaders(hsts: !app.Environment.IsDevelopment());
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseRateLimiter();
app.UseCsrfProtection();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/health/ready", async (BankDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503)).AllowAnonymous();
app.MapBankApi();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    await using (var scope = app.Services.CreateAsyncScope())
        await scope.ServiceProvider.GetRequiredService<BankDbContext>().Database.MigrateAsync();
    await DataSeeder.SeedReferenceAsync(app.Services);
    if (builder.Configuration.GetValue<bool>("Seed:Demo"))
        await DataSeeder.SeedDemoAsync(app.Services,
            builder.Configuration["Seed:DemoPassword"] ?? throw new InvalidOperationException("Seed:DemoPassword em falta."),
            builder.Configuration["Seed:DemoPin"] ?? throw new InvalidOperationException("Seed:DemoPin em falta."));
}
else
{
    await DataSeeder.SeedReferenceAsync(app.Services);
}

app.Run();

public partial class Program;

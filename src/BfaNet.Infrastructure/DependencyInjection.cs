using BfaNet.Application;
using BfaNet.Application.Abstractions;
using BfaNet.Infrastructure.Persistence;
using BfaNet.Infrastructure.Security;
using BfaNet.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BfaNet.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection s, IConfiguration cfg)
    {
        s.AddOptions<SecurityOptions>().Bind(cfg.GetSection(SecurityOptions.Section));
        s.AddOptions<BankingOptions>().Bind(cfg.GetSection(BankingOptions.Section));
        s.AddOptions<AssistantOptions>().Bind(cfg.GetSection(AssistantOptions.Section));

        s.AddSingleton(TimeProvider.System);
        s.AddSingleton<IFieldEncryptor, AesGcmFieldEncryptor>();
        s.AddSingleton<IBlindIndex, HmacBlindIndex>();
        s.AddSingleton<ISecretHasher, Argon2SecretHasher>();
        s.AddSingleton<ITokenService, TokenService>();

        var connection = cfg.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default não configurada.");
        s.AddDbContextFactory<BankDbContext>((sp, o) => o
            .UseNpgsql(connection, n => n.EnableRetryOnFailure(0))
            .UseSnakeCaseNamingConvention(), ServiceLifetime.Scoped);
        s.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<BankDbContext>>().CreateDbContext());

        s.AddScoped<CredentialAttempts>();
        s.AddScoped<PinAuthorizer>();
        s.AddScoped<LedgerPoster>();
        s.AddScoped<IAuditWriter, AuditWriter>();
        s.AddScoped<IAuthService, AuthService>();
        s.AddScoped<IAccountService, AccountService>();
        s.AddScoped<ITransferService, TransferService>();
        s.AddScoped<IBeneficiaryService, BeneficiaryService>();
        s.AddScoped<ICardService, CardService>();
        s.AddScoped<IPublicInfoService, PublicInfoService>();
        s.AddScoped<IAssistantService, AssistantService>();
        s.AddScoped<IAvatarService, AvatarService>();
        s.AddScoped<IReceiptPdfService, ReceiptPdfService>();
        s.AddScoped<IStatementPdfService, StatementPdfService>();

        s.AddHttpClient(ExchangeRateRefresher.ClientName, c =>
        {
            c.BaseAddress = new Uri("https://www.bfa.ao");
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("BFANET-Reference/1.0");
        });
        s.AddHostedService<ExchangeRateRefresher>();

        return s;
    }
}

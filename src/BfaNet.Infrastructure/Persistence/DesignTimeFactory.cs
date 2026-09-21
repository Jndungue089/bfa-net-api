using BfaNet.Application;
using BfaNet.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;

namespace BfaNet.Infrastructure.Persistence;

/// <summary>Used by `dotnet ef` only. Throw-away key; nothing is ever encrypted at design time.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<BankDbContext>
{
    public BankDbContext CreateDbContext(string[] args)
    {
        var enc = new AesGcmFieldEncryptor(Options.Create(new SecurityOptions
        {
            EncryptionKeys = { ["k1"] = Convert.ToBase64String(new byte[32]) }
        }));
        var opts = new DbContextOptionsBuilder<BankDbContext>()
            .UseNpgsql("Host=localhost;Database=bfanet_design")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new BankDbContext(opts, enc);
    }
}

using System.Security.Cryptography;
using BfaNet.Application;
using BfaNet.Application.Abstractions;
using BfaNet.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BfaNet.Infrastructure.Security;

public sealed class TokenService(IOptions<SecurityOptions> options, TimeProvider clock) : ITokenService
{
    private readonly SecurityOptions _o = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(Customer customer, Guid familyId)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(_o.AccessTokenMinutes);
        var key = new SymmetricSecurityKey(Convert.FromBase64String(_o.JwtSigningKey));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _o.JwtIssuer,
            Audience = _o.JwtAudience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            // Only opaque identifiers — no PII inside tokens.
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = customer.Id.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
                ["sid"] = familyId.ToString()
            },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };
        return (_handler.CreateToken(descriptor), expires);
    }

    public (string Raw, string Hash) CreateRefreshToken()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (raw, HashRefreshToken(raw));
    }

    public string HashRefreshToken(string raw) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
}

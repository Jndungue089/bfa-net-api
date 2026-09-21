using System.Security.Cryptography;
using System.Text;
using BfaNet.Application;
using BfaNet.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace BfaNet.Infrastructure.Security;

public sealed class HmacBlindIndex : IBlindIndex
{
    private readonly byte[] _key;

    public HmacBlindIndex(IOptions<SecurityOptions> options)
    {
        _key = Convert.FromBase64String(options.Value.BlindIndexKey);
        if (_key.Length < 32) throw new InvalidOperationException("Security:BlindIndexKey deve ter pelo menos 32 bytes.");
    }

    /// <summary>Purpose is mixed in so equal values in different columns yield unrelated indexes.</summary>
    public string Compute(string purpose, string normalizedValue) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{purpose}\0{normalizedValue}")));
}

using System.Security.Cryptography;
using System.Text;
using BfaNet.Application;
using BfaNet.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace BfaNet.Infrastructure.Security;

public sealed class AesGcmFieldEncryptor : IFieldEncryptor
{
    private const string Version = "v1";
    private const int NonceSize = 12, TagSize = 16;
    private readonly Dictionary<string, byte[]> _keys = [];
    private readonly string _activeId;

    public AesGcmFieldEncryptor(IOptions<SecurityOptions> options)
    {
        var o = options.Value;
        foreach (var (id, b64) in o.EncryptionKeys)
        {
            var key = Convert.FromBase64String(b64);
            if (key.Length != 32) throw new InvalidOperationException($"A chave de cifra '{id}' deve ter 32 bytes.");
            _keys[id] = key;
        }
        if (!_keys.ContainsKey(o.ActiveEncryptionKeyId))
            throw new InvalidOperationException("Chave de cifra activa não configurada (Security:EncryptionKeys).");
        _activeId = o.ActiveEncryptionKeyId;
    }

    public string Encrypt(string plaintext, string context)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_keys[_activeId], TagSize);
        aes.Encrypt(nonce, data, cipher, tag, Encoding.UTF8.GetBytes(context));

        var blob = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipher.CopyTo(blob, NonceSize + TagSize);
        return $"{Version}.{_activeId}.{Convert.ToBase64String(blob)}";
    }

    public string Decrypt(string envelope, string context)
    {
        var parts = envelope.Split('.', 3);
        if (parts.Length != 3 || parts[0] != Version || !_keys.TryGetValue(parts[1], out var key))
            throw new CryptographicException("Envelope de cifra inválido.");

        var blob = Convert.FromBase64String(parts[2]);
        if (blob.Length < NonceSize + TagSize) throw new CryptographicException("Envelope de cifra inválido.");

        var plain = new byte[blob.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize),
            plain, Encoding.UTF8.GetBytes(context));
        return Encoding.UTF8.GetString(plain);
    }
}

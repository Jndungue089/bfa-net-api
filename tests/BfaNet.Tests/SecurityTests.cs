using System.Security.Cryptography;
using BfaNet.Application;
using BfaNet.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Xunit;

namespace BfaNet.Tests;

public class EncryptionTests
{
    private static AesGcmFieldEncryptor Make(params (string id, byte fill)[] keys) => new(Options.Create(new SecurityOptions
    {
        EncryptionKeys = keys.ToDictionary(k => k.id, k => Convert.ToBase64String(Enumerable.Repeat(k.fill, 32).ToArray())),
        ActiveEncryptionKeyId = keys[^1].id
    }));

    [Fact]
    public void Roundtrip() => Assert.Equal("maria@example.ao", Make(("k1", 1)).Decrypt(Make(("k1", 1)).Encrypt("maria@example.ao", "c"), "c"));

    [Fact]
    public void Same_plaintext_encrypts_differently_each_time()
    {
        var e = Make(("k1", 1));
        Assert.NotEqual(e.Encrypt("x", "c"), e.Encrypt("x", "c"));
    }

    [Fact]
    public void Ciphertext_is_bound_to_its_column()
    {
        var e = Make(("k1", 1));
        Assert.ThrowsAny<CryptographicException>(() => e.Decrypt(e.Encrypt("secret", "customers.email"), "customers.phone"));
    }

    [Fact]
    public void Tampering_is_detected()
    {
        var e = Make(("k1", 1));
        var env = e.Encrypt("secret", "c");
        var parts = env.Split('.', 3);
        var blob = Convert.FromBase64String(parts[2]);
        blob[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => e.Decrypt($"{parts[0]}.{parts[1]}.{Convert.ToBase64String(blob)}", "c"));
    }

    [Fact]
    public void Key_rotation_old_data_still_decrypts()
    {
        var old = Make(("k1", 1));
        var envelope = old.Encrypt("dado antigo", "c");
        var rotated = Make(("k1", 1), ("k2", 2)); // k2 active, k1 retained
        Assert.Equal("dado antigo", rotated.Decrypt(envelope, "c"));
        Assert.StartsWith("v1.k2.", rotated.Encrypt("novo", "c"));
    }

    [Fact]
    public void Rejects_short_keys() =>
        Assert.Throws<InvalidOperationException>(() => new AesGcmFieldEncryptor(Options.Create(new SecurityOptions
        { EncryptionKeys = { ["k1"] = Convert.ToBase64String(new byte[16]) } })));
}

public class HashingTests
{
    [Fact]
    public void Argon2_verifies_and_salts()
    {
        var h = new Argon2SecretHasher();
        var a = h.Hash("Boa#Passe-2026x");
        Assert.NotEqual(a, h.Hash("Boa#Passe-2026x"));
        Assert.StartsWith("$argon2id$", a);
        Assert.True(h.Verify("Boa#Passe-2026x", a));
        Assert.False(h.Verify("boa#passe-2026x", a));
        Assert.False(h.Verify("x", "not-a-hash"));
    }

    [Fact]
    public void Blind_index_is_deterministic_keyed_and_purpose_bound()
    {
        var k1 = new HmacBlindIndex(Options.Create(new SecurityOptions { BlindIndexKey = Convert.ToBase64String(new byte[32]) }));
        var k2 = new HmacBlindIndex(Options.Create(new SecurityOptions { BlindIndexKey = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()) }));
        Assert.Equal(k1.Compute("email", "a@b.ao"), k1.Compute("email", "a@b.ao"));
        Assert.NotEqual(k1.Compute("email", "a@b.ao"), k2.Compute("email", "a@b.ao"));
        Assert.NotEqual(k1.Compute("email", "923456789"), k1.Compute("phone", "923456789"));
        Assert.Equal(64, k1.Compute("email", "a@b.ao").Length);
    }
}

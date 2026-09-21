using System.Security.Cryptography;
using System.Text;
using BfaNet.Application.Abstractions;
using Konscious.Security.Cryptography;

namespace BfaNet.Infrastructure.Security;

/// <summary>Argon2id (RFC 9106 second recommended profile, tuned up) for passwords and PINs.</summary>
public sealed class Argon2SecretHasher : ISecretHasher
{
    private const int SaltLen = 16, HashLen = 32, MemoryKiB = 65536, Iterations = 3, Parallelism = 2;
    private static readonly string DummyHash = new Argon2SecretHasher().Hash("dummy-secret-for-timing");

    public string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var hash = Derive(secret, salt, MemoryKiB, Iterations, Parallelism, HashLen);
        return $"$argon2id$v=19$m={MemoryKiB},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string secret, string stored)
    {
        try
        {
            var p = stored.Split('$', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 5 || p[0] != "argon2id") return false;
            var cfg = p[2].Split(',').Select(x => int.Parse(x[2..])).ToArray();
            var salt = Convert.FromBase64String(p[3]);
            var expected = Convert.FromBase64String(p[4]);
            var actual = Derive(secret, salt, cfg[0], cfg[1], cfg[2], expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    public void DummyVerify() => Verify("wrong-secret", DummyHash);

    private static byte[] Derive(string secret, byte[] salt, int memKiB, int iterations, int parallelism, int len)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(secret))
        {
            Salt = salt, MemorySize = memKiB, Iterations = iterations, DegreeOfParallelism = parallelism
        };
        return argon.GetBytes(len);
    }
}

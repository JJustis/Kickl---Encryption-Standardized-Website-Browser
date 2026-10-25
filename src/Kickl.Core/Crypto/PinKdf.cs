using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace Kickl.Core.Crypto;

/// <summary>
/// Versioned PIN KDFs. Argon2id is used for new profiles; PBKDF2 remains only to unlock
/// and migrate existing slice profiles.
/// </summary>
public static class PinKdf
{
    public const string Id = "argon2id-v19-p4";
    public const string LegacyArgon2Id = "argon2id-v19";
    public const string LegacyId = "pbkdf2-sha256-slice";
    public const int MemoryKiB = 64 * 1024;
    public const int Passes = 3;
    public const int Parallelism = 4;
    public const int LegacyArgon2Parallelism = 1;
    public const int LegacyIterations = 210_000;
    public const int SaltSize = 16;
    public const int KeySize = 32;

    public static byte[] Derive(string pin, ReadOnlySpan<byte> salt, int length = KeySize) =>
        Derive(pin, Id, salt, length, MemoryKiB, Passes, Parallelism, LegacyIterations);

    public static byte[] Derive(string pin, string id, ReadOnlySpan<byte> salt, int length,
        int memoryKiB = MemoryKiB, int passes = Passes, int parallelism = Parallelism,
        int legacyIterations = LegacyIterations)
    {
        if (string.IsNullOrEmpty(pin)) throw new ArgumentException("PIN must not be empty.", nameof(pin));
        if (salt.Length != SaltSize) throw new ArgumentException("PIN KDF salt must be 16 bytes.", nameof(salt));
        if (length is < 16 or > 64) throw new ArgumentOutOfRangeException(nameof(length));

        if (id == LegacyId)
        {
            if (legacyIterations != LegacyIterations)
                throw new ArgumentOutOfRangeException(nameof(legacyIterations));
            return Rfc2898DeriveBytes.Pbkdf2(pin, salt, legacyIterations, HashAlgorithmName.SHA256, length);
        }

        var supportedArgonParallelism = id switch
        {
            Id => Parallelism,
            LegacyArgon2Id => LegacyArgon2Parallelism,
            _ => 0
        };
        if (supportedArgonParallelism == 0 || memoryKiB != MemoryKiB || passes != Passes ||
            parallelism != supportedArgonParallelism)
            throw new ArgumentException("Unsupported PIN KDF or parameters.", nameof(id));

        var password = Encoding.UTF8.GetBytes(pin);
        try
        {
            var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
                .WithVersion(Argon2Parameters.Version13)
                .WithIterations(Passes)
                .WithMemoryAsKB(MemoryKiB)
                .WithParallelism(supportedArgonParallelism)
                .WithSalt(salt.ToArray())
                .Build();
            var output = new byte[length];
            var generator = new Argon2BytesGenerator();
            generator.Init(parameters);
            generator.GenerateBytes(password, output);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }
}

public static class Hkdf
{
    public static byte[] Derive(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, string info, int length = 32) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, ikm.ToArray(), length, salt.ToArray(),
            System.Text.Encoding.UTF8.GetBytes(info));
}

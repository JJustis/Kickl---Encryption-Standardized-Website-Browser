using System.Security.Cryptography;
using System.Text.Json;
using Kickl.Core.Util;

namespace Kickl.Core.Crypto;

public sealed class InvalidUserKeyException : Exception
{
    public InvalidUserKeyException() : base("The user key is incorrect or the keystore is damaged.") { }
}

public sealed class KeystoreFile
{
    public int V { get; set; } = 1;
    public string Kdf { get; set; } = PinKdf.Id;
    public int Iterations { get; set; }
    public int MemoryKiB { get; set; }
    public int Passes { get; set; }
    public int Parallelism { get; set; }
    public int SaltSize { get; set; } = PinKdf.SaltSize;
    public int KeySize { get; set; } = Aead.KeySize;
    public byte[] Salt { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] WrappedMaster { get; set; } = [];
    public string CreatedUtc { get; set; } = "";
}

/// <summary>
/// Creates and unlocks the per-profile master key. The PIN-derived KEK wraps the master
/// with AES-256-GCM; the master itself never touches disk in plaintext.
/// </summary>
public static class Keystore
{
    private static readonly byte[] WrapAad = Utf8.B("kickl/v1/keystore");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static bool Exists(DataPaths paths) => File.Exists(paths.Keystore);

    public static void Create(DataPaths paths, string pin)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrEmpty(pin)) throw new ArgumentException("PIN must not be empty.", nameof(pin));
        paths.Ensure();
        if (File.Exists(paths.Keystore))
            throw new IOException("A keystore already exists for this profile.");

        var master = RandomNumberGenerator.GetBytes(Aead.KeySize);
        var salt = RandomNumberGenerator.GetBytes(PinKdf.SaltSize);
        byte[]? kek = null;
        string? temporaryPath = null;
        try
        {
            kek = PinKdf.Derive(pin, PinKdf.Id, salt, PinKdf.KeySize);
            var (nonce, wrapped) = Aead.Seal(kek, master, WrapAad);
            var file = new KeystoreFile
            {
                Salt = salt,
                Nonce = nonce,
                WrappedMaster = wrapped,
                MemoryKiB = PinKdf.MemoryKiB,
                Passes = PinKdf.Passes,
                Parallelism = PinKdf.Parallelism,
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
            };

            temporaryPath = paths.Keystore + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, file, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, paths.Keystore);
            FileProtection.Restrict(Path.GetDirectoryName(paths.Keystore)!);
            FileProtection.Restrict(paths.Keystore);
            temporaryPath = null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
            if (kek is not null) CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(salt);
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static byte[] Unlock(DataPaths paths, string pin)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrEmpty(pin)) throw new InvalidUserKeyException();
        if (!File.Exists(paths.Keystore))
            throw new InvalidUserKeyException();

        KeystoreFile? file;
        try { file = JsonSerializer.Deserialize<KeystoreFile>(File.ReadAllText(paths.Keystore)); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { throw new InvalidUserKeyException(); }

        if (file is null || file.V != 1 || !IsSupportedKdf(file) || file.SaltSize != PinKdf.SaltSize ||
            file.KeySize != Aead.KeySize || file.Salt is null || file.Salt.Length != PinKdf.SaltSize ||
            file.Nonce is null || file.Nonce.Length != Aead.NonceSize || file.WrappedMaster is null ||
            file.WrappedMaster.Length != Aead.KeySize + Aead.TagSize)
            throw new InvalidUserKeyException();

        byte[] kek;
        try
        {
            kek = PinKdf.Derive(pin, file.Kdf, file.Salt, PinKdf.KeySize,
                file.MemoryKiB, file.Passes, file.Parallelism, file.Iterations);
        }
        catch (ArgumentException) { throw new InvalidUserKeyException(); }
        try
        {
            var master = Aead.Open(kek, file.Nonce, file.WrappedMaster, WrapAad);
            if (master.Length != Aead.KeySize)
            {
                CryptographicOperations.ZeroMemory(master);
                throw new InvalidUserKeyException();
            }
            if (file.Kdf != PinKdf.Id)
                TryMigrate(paths, pin, master);
            return master;
        }
        catch (CryptographicException)
        {
            throw new InvalidUserKeyException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static bool IsSupportedKdf(KeystoreFile file) => file.Kdf switch
    {
        PinKdf.Id => file.Iterations == 0 && file.MemoryKiB == PinKdf.MemoryKiB &&
                     file.Passes == PinKdf.Passes && file.Parallelism == PinKdf.Parallelism,
        PinKdf.LegacyArgon2Id => file.Iterations == 0 && file.MemoryKiB == PinKdf.MemoryKiB &&
                                 file.Passes == PinKdf.Passes && file.Parallelism == PinKdf.LegacyArgon2Parallelism,
        PinKdf.LegacyId => file.Iterations == PinKdf.LegacyIterations && file.MemoryKiB == 0 &&
                           file.Passes == 0 && file.Parallelism == 0,
        _ => false
    };

    private static void TryMigrate(DataPaths paths, string pin, byte[] master)
    {
        byte[]? salt = null;
        byte[]? kek = null;
        string? temporaryPath = null;
        try
        {
            salt = RandomNumberGenerator.GetBytes(PinKdf.SaltSize);
            kek = PinKdf.Derive(pin, PinKdf.Id, salt, PinKdf.KeySize);
            var (nonce, wrapped) = Aead.Seal(kek, master, WrapAad);
            var file = new KeystoreFile
            {
                Kdf = PinKdf.Id,
                Salt = salt,
                Nonce = nonce,
                WrappedMaster = wrapped,
                MemoryKiB = PinKdf.MemoryKiB,
                Passes = PinKdf.Passes,
                Parallelism = PinKdf.Parallelism,
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
            };

            temporaryPath = paths.Keystore + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, file, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Replace(temporaryPath, paths.Keystore, destinationBackupFileName: null);
            FileProtection.Restrict(paths.Keystore);
            temporaryPath = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Migration is opportunistic; the successfully unwrapped master remains usable.
        }
        finally
        {
            if (salt is not null) CryptographicOperations.ZeroMemory(salt);
            if (kek is not null) CryptographicOperations.ZeroMemory(kek);
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

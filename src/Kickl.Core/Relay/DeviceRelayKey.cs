using System.Security.Cryptography;
using System.Text.Json;
using Kickl.Core.Crypto;
using Kickl.Core.Util;

namespace Kickl.Core.Relay;

/// <summary>
/// The origin device's stable RSA identity for direct article delivery. The public half is
/// published in the registry; the private half is AES-GCM wrapped under the profile master key
/// and never leaves this device in plaintext.
/// </summary>
public sealed class RelayIdentity : IDisposable
{
    internal RelayIdentity(RSA rsa, string deviceId, byte[] publicKeySpki)
    {
        Rsa = rsa;
        DeviceId = deviceId;
        PublicKeySpki = publicKeySpki;
    }

    public RSA Rsa { get; }
    public string DeviceId { get; }
    public byte[] PublicKeySpki { get; }

    public void Dispose() => Rsa.Dispose();
}

public static class DeviceRelayKey
{
    private const string Info = "kickl/v1/relay-key";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static RelayIdentity Ensure(DataPaths paths, byte[] master)
    {
        if (File.Exists(paths.RelayKey))
            return Load(paths, master);

        Directory.CreateDirectory(Path.GetDirectoryName(paths.RelayKey)!);
        var deviceId = "kb1d" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var rsa = RSA.Create(3072);
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        var spki = rsa.ExportSubjectPublicKeyInfo();
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = Hkdf.Derive(master, salt, Info);
        var (nonce, wrapped) = Aead.Seal(kek, pkcs8, Utf8.B(Info + "|" + deviceId));
        CryptographicOperations.ZeroMemory(kek);
        CryptographicOperations.ZeroMemory(pkcs8);

        var file = new RelayFile
        {
            DeviceId = deviceId,
            Salt = salt,
            Nonce = nonce,
            WrappedPrivateKey = wrapped,
            PublicKeySpki = spki,
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
        };
        var temp = paths.RelayKey + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Json));
        File.Move(temp, paths.RelayKey, overwrite: true);
        return new RelayIdentity(rsa, deviceId, spki);
    }

    /// <summary>
    /// The device id the registry publishes, readable without the profile master key. Used to
    /// tell whether a site's origin is this device (another local profile) before opening the tunnel.
    /// </summary>
    public static bool TryReadDeviceId(DataPaths paths, out string deviceId)
    {
        deviceId = "";
        try
        {
            var file = JsonSerializer.Deserialize<RelayFile>(File.ReadAllText(paths.RelayKey));
            if (file is null || file.V != 1 || string.IsNullOrEmpty(file.DeviceId)) return false;
            deviceId = file.DeviceId;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static RelayIdentity Load(DataPaths paths, byte[] master)
    {
        RelayFile? file;
        try
        {
            file = JsonSerializer.Deserialize<RelayFile>(File.ReadAllText(paths.RelayKey));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            throw new InvalidOperationException("The device relay key is unreadable.", ex);
        }

        if (file is null || file.V != 1 || file.WrappedPrivateKey.Length == 0)
            throw new InvalidOperationException("The device relay key is missing or damaged.");

        var kek = Hkdf.Derive(master, file.Salt, Info);
        byte[] pkcs8;
        try
        {
            pkcs8 = Aead.Open(kek, file.Nonce, file.WrappedPrivateKey,
                Utf8.B(Info + "|" + file.DeviceId));
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("The device relay key failed authentication.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }

        try
        {
            var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
            return new RelayIdentity(rsa, file.DeviceId, file.PublicKeySpki);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private sealed class RelayFile
    {
        public int V { get; set; } = 1;
        public string DeviceId { get; set; } = "";
        public byte[] Salt { get; set; } = [];
        public byte[] Nonce { get; set; } = [];
        public byte[] WrappedPrivateKey { get; set; } = [];
        public byte[] PublicKeySpki { get; set; } = [];
        public string CreatedUtc { get; set; } = "";
    }
}

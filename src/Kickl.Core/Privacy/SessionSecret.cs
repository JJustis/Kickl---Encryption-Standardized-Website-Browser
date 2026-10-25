using System.Security.Cryptography;
using Kickl.Core.Crypto;

namespace Kickl.Core.Privacy;

/// <summary>
/// The per-unlock session secret. Derived with HKDF from the vault master and a fresh
/// per-unlock nonce, held in memory only, zeroised on disposal.
/// </summary>
public sealed class SessionSecret : IDisposable
{
    private byte[]? _key;
    private ECDiffieHellman? _identity;

    private SessionSecret(byte[] key, byte[] salt, byte[] id, ECDiffieHellman identity)
    {
        _key = key;
        Salt = salt;
        Id = id;
        _identity = identity;
    }

    public byte[] Salt { get; }
    public byte[] Id { get; }
    public bool IsAlive => _key is not null;
    public string IdHex => Convert.ToHexString(Id).ToLowerInvariant();

    public ReadOnlySpan<byte> Key =>
        _key ?? throw new ObjectDisposedException(nameof(SessionSecret), "The session is locked.");

    /// <summary>
    /// P-256 identity for this browser opening. Fresh on every unlock, never persisted,
    /// disposed with the session. It is the "temporal" half of the key model: the long-term
    /// ownership key proves who you are; this key exists only for this opening.
    /// </summary>
    public byte[] SessionPublicKeySpki =>
        _identity?.ExportSubjectPublicKeyInfo()
        ?? throw new ObjectDisposedException(nameof(SessionSecret), "The session is locked.");

    public static SessionSecret Create(ReadOnlySpan<byte> master)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var key = Hkdf.Derive(master, salt, "kickl/v1/session", 32);
        var id = SHA256.HashData(key)[..16];
        var identity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return new SessionSecret(key, salt, id, identity);
    }

    public void Dispose()
    {
        _identity?.Dispose();
        _identity = null;
        if (_key is null) return;
        CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }
}

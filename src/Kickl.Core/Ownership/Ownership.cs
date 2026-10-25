using System.Security.Cryptography;
using System.Text.Json;
using Kickl.Core.Crypto;
using Kickl.Core.Registry;
using Kickl.Core.Util;

namespace Kickl.Core.Ownership;

/// <summary>
/// What lives on the owner's USB stick. Never uploaded, never in the profile directory.
/// The RSA private key unwraps the AES-256 ownership token (RSA-OAEP); the same RSA key
/// signs title statements. The token itself is only ever stored RSA-wrapped.
/// </summary>
public sealed class OwnerSecret
{
    public int V { get; set; } = 1;
    public string Site { get; set; } = "";
    public string RsaPrivateKeyPkcs8 { get; set; } = "";
    /// <summary>RSA-OAEP-SHA256 wrap of the random AES-256 data key.</summary>
    public string WrappedKey { get; set; } = "";
    /// <summary>AES-256-GCM nonce for the token.</summary>
    public byte[] TokenNonce { get; set; } = [];
    /// <summary>AES-256-GCM ciphertext of the ownership token.</summary>
    public byte[] TokenCiphertext { get; set; } = [];
    public OwnershipPublic Public { get; set; } = new();
    public string CreatedUtc { get; set; } = "";
}

/// <summary>The public half: safe to upload to secupgrade.com.</summary>
public sealed class OwnershipPublic
{
    public int V { get; set; } = 1;
    public string Site { get; set; } = "";
    public string RsaPublicKeySpki { get; set; } = "";
    public string TokenCommitment { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

/// <summary>
/// Presented inside the ownership tunnel. The token is the AES-256 secret; the RSA signature
/// proves the USB key is present. In production the token never leaves an HPKE/Noise tunnel.
/// </summary>
public sealed class OwnershipProof
{
    public int V { get; set; } = 1;
    public string Site { get; set; } = "";
    public string Challenge { get; set; } = "";
    public string Token { get; set; } = "";
    public string RsaSignature { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class TitleRecord
{
    public int V { get; set; } = 1;
    public string Site { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string RsaPublicKeySpki { get; set; } = "";
    public string TokenCommitment { get; set; } = "";
    public string BadgeId { get; set; } = "";
    public int Seq { get; set; } = 1;
    public string Status { get; set; } = "active"; // active | listed | transferred | lapsed | closed
    public string Tier { get; set; } = "paid-simulated";
    public string PaymentRef { get; set; } = "simulated";
    public string IssuedUtc { get; set; } = "";
    public string ExpiresUtc { get; set; } = "";
    public string Issuer { get; set; } = "secupgrade.com (slice)";
    public string IssuerSig { get; set; } = "dev-unsigned";
}

public sealed class TransferStatement
{
    public int V { get; set; } = 1;
    public string Site { get; set; } = "";
    public string FromOwnerId { get; set; } = "";
    public string ToOwnerId { get; set; } = "";
    public string ToRsaPublicKeySpki { get; set; } = "";
    public string ToTokenCommitment { get; set; } = "";
    public int Seq { get; set; }
    public string Price { get; set; } = "0";
    public string CreatedUtc { get; set; } = "";
    public string SellerSignature { get; set; } = "";
}

/// <summary>Encrypted owner key file stored on the trusted owner's removable device.</summary>
public sealed class OwnerKeyEnvelope
{
    public int V { get; set; } = 2;
    public string Site { get; set; } = "";
    public string Kdf { get; set; } = PinKdf.Id;
    public byte[] Salt { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public OwnershipPublic Public { get; set; } = new();
}

/// <summary>RSA-OAEP wrapped AES-256-GCM proof packet for the paired receiver.</summary>
public sealed class PairedDonglePacket
{
    public int V { get; set; } = 1;
    public string Site { get; set; } = "";
    public string RecipientId { get; set; } = "";
    public string WrappedAesKey { get; set; } = "";
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
}

public sealed class PairedDongleIdentity
{
    public string Id { get; set; } = "";
    public string RsaPublicKeySpki { get; set; } = "";
    public string RsaPrivateKeyPkcs8 { get; set; } = "";
}

public sealed class PairedDongleIdentityEnvelope
{
    public int V { get; set; } = 1;
    public string Id { get; set; } = "";
    public string RsaPublicKeySpki { get; set; } = "";
    public string Kdf { get; set; } = PinKdf.Id;
    public byte[] Salt { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
}

public sealed class PairedDonglePin
{
    public int V { get; set; } = 1;
    public string Id { get; set; } = "";
    public string RsaPublicKeySpki { get; set; } = "";
}

public sealed class PairedDongleAck
{
    public int V { get; set; } = 1;
    public string ReceiverId { get; set; } = "";
    public string Site { get; set; } = "";
    public string Challenge { get; set; } = "";
    public bool Accepted { get; set; }
    public string Signature { get; set; } = "";
}

public sealed class PairedDongleHelloRequest
{
    public int V { get; set; } = 1;
    public string Challenge { get; set; } = "";
}

public sealed class PairedDongleHello
{
    public int V { get; set; } = 1;
    public string ReceiverId { get; set; } = "";
    public string RsaPublicKeySpki { get; set; } = "";
    public string Challenge { get; set; } = "";
    public string Signature { get; set; } = "";
}

public sealed class PairedDonglePinRecord
{
    public int V { get; set; } = 1;
    public string TransportKey { get; set; } = "";
    public string ReceiverId { get; set; } = "";
    public string RsaPublicKeySpki { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string PinnedUtc { get; set; } = "";
}

/// <summary>
/// Local stand-in for the secupgrade.com title authority. The ceremony is real:
/// challenge → unwrap AES token with the USB RSA key → token sent through the (simulated)
/// tunnel → badge issued. Transfer is a seller-signed statement accepted by the buyer's key.
/// Payment and the network tunnel are simulated at this stage.
/// </summary>
public static class OwnershipAuthority
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string SecretPath(string usbDir, string site) =>
        Path.Combine(usbDir, $"kickl-owner-{site}.key");

    public static string PublicPath(string usbDir, string site) =>
        Path.Combine(usbDir, $"kickl-owner-{site}.pub.json");

    private static string SharedDir(DataPaths paths) => Path.Combine(paths.Root, "shared", "ownership");

    public static string PublicPath(DataPaths paths, string site) =>
        Path.Combine(SharedDir(paths), $"{site}.pub.json");

    public static string TitlePath(DataPaths paths, string site) =>
        Path.Combine(SharedDir(paths), $"{site}.title.json");

    // ------------------------------------------------------------ key ceremony

    public static OwnershipPublic Create(DataPaths paths, string site, string usbDir, string passphrase, string displayName = "")
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 10)
            throw new ArgumentException("Owner-key passphrase must be at least 10 characters.", nameof(passphrase));
        Directory.CreateDirectory(usbDir);
        Directory.CreateDirectory(SharedDir(paths));

        using var rsa = RSA.Create(3072);
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        var spki = rsa.ExportSubjectPublicKeyInfo();
        var token = RandomNumberGenerator.GetBytes(32);
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var tokenAad = Utf8.B("kickl/v1/owner-token|" + site);

        try
        {
            var tokenB64 = Convert.ToBase64String(token);
            var pub = new OwnershipPublic
            {
                Site = site,
                RsaPublicKeySpki = Convert.ToBase64String(spki),
                TokenCommitment = TokenCommitmentFor(tokenB64),
                OwnerId = OwnerIdFor(spki),
                DisplayName = displayName,
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
            };

            var (tokenNonce, tokenCiphertext) = Aead.Seal(dataKey, token, tokenAad);
            var secret = new OwnerSecret
            {
                Site = site,
                RsaPrivateKeyPkcs8 = Convert.ToBase64String(pkcs8),
                WrappedKey = Convert.ToBase64String(rsa.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256)),
                TokenNonce = tokenNonce,
                TokenCiphertext = tokenCiphertext,
                Public = pub,
                CreatedUtc = pub.CreatedUtc
            };

            var salt = RandomNumberGenerator.GetBytes(PinKdf.SaltSize);
            var key = PinKdf.Derive(passphrase, salt);
            try
            {
                var plaintext = JsonSerializer.SerializeToUtf8Bytes(secret, Json);
                try
                {
                    var (nonce, ciphertext) = Aead.Seal(key, plaintext, Utf8.B("kickl/v2/owner-key|" + site));
                    var envelope = new OwnerKeyEnvelope { Site = site, Salt = salt, Nonce = nonce,
                        Ciphertext = ciphertext, Public = pub };
                    File.WriteAllText(SecretPath(usbDir, site), JsonSerializer.Serialize(envelope, Json));
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }
            finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(salt); }
            RestrictToOwner(SecretPath(usbDir, site));
            File.WriteAllText(PublicPath(usbDir, site), JsonSerializer.Serialize(pub, Json));
            File.WriteAllText(PublicPath(paths, site), JsonSerializer.Serialize(pub, Json));

            return pub;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
            CryptographicOperations.ZeroMemory(token);
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public static OwnerSecret LoadSecret(string usbDir, string site, string passphrase)
    {
        var path = SecretPath(usbDir, site);
        if (!File.Exists(path)) throw new FileNotFoundException("The owner key is not on this USB path.", path);
        var envelope = JsonSerializer.Deserialize<OwnerKeyEnvelope>(File.ReadAllText(path), Json)
                       ?? throw new InvalidOperationException("The owner key envelope is unreadable.");
        if (envelope.V != 2 || envelope.Kdf != PinKdf.Id || envelope.Salt.Length != PinKdf.SaltSize ||
            envelope.Nonce.Length != Aead.NonceSize || envelope.Ciphertext.Length < Aead.TagSize)
            throw new InvalidOperationException("Unsupported or malformed owner-key envelope.");
        if (!string.Equals(envelope.Site, site, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This USB key belongs to a different name.");
        var key = PinKdf.Derive(passphrase, envelope.Salt);
        try
        {
            var plaintext = Aead.Open(key, envelope.Nonce, envelope.Ciphertext, Utf8.B("kickl/v2/owner-key|" + site));
            try
            {
                var secret = JsonSerializer.Deserialize<OwnerSecret>(plaintext, Json)
                             ?? throw new InvalidOperationException("The decrypted owner key is unreadable.");
                if (!string.Equals(secret.Site, site, StringComparison.OrdinalIgnoreCase) ||
                    secret.Public.OwnerId != envelope.Public.OwnerId)
                    throw new CryptographicException("Owner-key metadata does not match its encrypted payload.");
                return secret;
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static PairedDongleIdentity CreatePairedIdentity()
    {
        using var rsa = RSA.Create(3072);
        return new PairedDongleIdentity { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            RsaPublicKeySpki = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()),
            RsaPrivateKeyPkcs8 = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()) };
    }

    public static PairedDongleHelloRequest NewPairedDongleHelloRequest() =>
        new() { Challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() };

    public static PairedDongleHello CreatePairedDongleHello(
        PairedDongleIdentity identity, PairedDongleHelloRequest request)
    {
        if (request.V != 1 || !IsChallenge(request.Challenge))
            throw new CryptographicException("Invalid paired receiver hello challenge.");
        var hello = new PairedDongleHello { ReceiverId = identity.Id,
            RsaPublicKeySpki = identity.RsaPublicKeySpki, Challenge = request.Challenge };
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(identity.RsaPrivateKeyPkcs8), out _);
        hello.Signature = Convert.ToBase64String(rsa.SignData(Utf8.B(PairedHelloPayload(hello)),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        return hello;
    }

    public static string VerifyPairedDongleHello(PairedDongleHello hello, string challenge)
    {
        if (hello.V != 1 || !IsChallenge(challenge) || hello.Challenge != challenge ||
            string.IsNullOrWhiteSpace(hello.ReceiverId))
            throw new CryptographicException("Paired receiver hello challenge or identity is invalid.");
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(hello.RsaPublicKeySpki), out _);
        if (!rsa.VerifyData(Utf8.B(PairedHelloPayload(hello)), Convert.FromBase64String(hello.Signature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("Paired receiver hello signature is invalid.");
        return Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(hello.RsaPublicKeySpki)))
            .ToLowerInvariant();
    }

    public static string PinPairedDongle(string directory, string transportKey, PairedDongleHello hello,
        string challenge, bool userConfirmedFingerprint)
    {
        if (!userConfirmedFingerprint)
            throw new InvalidOperationException("First pairing requires the user to confirm the receiver fingerprint.");
        if (string.IsNullOrWhiteSpace(transportKey)) throw new ArgumentException("Transport key is required.", nameof(transportKey));
        var fingerprint = VerifyPairedDongleHello(hello, challenge);
        Directory.CreateDirectory(directory);
        var path = PairedDonglePinPath(directory, transportKey);
        var existing = LoadPairedDonglePin(directory, transportKey);
        if (existing is not null)
        {
            if (MatchesPinnedDongle(existing, hello, challenge)) return existing.Fingerprint;
            throw new CryptographicException("A different receiver is already pinned to this transport. Unpair it explicitly before replacing it.");
        }
        var pin = new PairedDonglePinRecord { TransportKey = transportKey,
            ReceiverId = hello.ReceiverId, RsaPublicKeySpki = hello.RsaPublicKeySpki,
            Fingerprint = fingerprint, PinnedUtc = DateTimeOffset.UtcNow.ToString("O") };
        File.WriteAllText(path, JsonSerializer.Serialize(pin, Json));
        RestrictToOwner(path);
        return fingerprint;
    }

    public static PairedDonglePinRecord? LoadPairedDonglePin(string directory, string transportKey)
    {
        var path = PairedDonglePinPath(directory, transportKey);
        if (!File.Exists(path)) return null;
        var pin = JsonSerializer.Deserialize<PairedDonglePinRecord>(File.ReadAllText(path), Json)
                  ?? throw new InvalidOperationException("Pinned dongle identity is unreadable.");
        if (pin.V != 1 || pin.TransportKey != transportKey ||
            pin.Fingerprint != Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(pin.RsaPublicKeySpki)))
                .ToLowerInvariant())
            throw new CryptographicException("Pinned dongle identity record is invalid.");
        return pin;
    }

    public static void ForgetPairedDongle(string directory, string transportKey)
    {
        if (string.IsNullOrWhiteSpace(transportKey)) throw new ArgumentException("Transport key is required.", nameof(transportKey));
        var path = PairedDonglePinPath(directory, transportKey);
        if (File.Exists(path)) File.Delete(path);
    }

    public static bool MatchesPinnedDongle(PairedDonglePinRecord pin, PairedDongleHello hello, string challenge)
    {
        var fingerprint = VerifyPairedDongleHello(hello, challenge);
        return pin.TransportKey.Length > 0 && pin.ReceiverId == hello.ReceiverId &&
        pin.RsaPublicKeySpki == hello.RsaPublicKeySpki && pin.Fingerprint ==
        fingerprint;
    }

    private static string PairedDonglePinPath(string directory, string transportKey) =>
        Path.Combine(directory, "receiver-pin-" + Convert.ToHexString(SHA256.HashData(Utf8.B(transportKey)))
            .ToLowerInvariant() + ".json");

    private static bool IsChallenge(string value)
    {
        try { return Convert.FromHexString(value).Length == 32; }
        catch (FormatException) { return false; }
    }

    private static string PairedHelloPayload(PairedDongleHello hello) =>
        $"kickl/v1/paired-hello|{hello.ReceiverId}|{hello.RsaPublicKeySpki}|{hello.Challenge}";

    /// <summary>
    /// Load a stable receiver identity encrypted under the trusted-device passphrase. A separate
    /// public pin file detects replacement of the encrypted receiver-key file when the pin remains.
    /// </summary>
    public static PairedDongleIdentity LoadOrCreatePairedIdentity(string directory, string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 10)
            throw new ArgumentException("Paired receiver passphrase must be at least 10 characters.", nameof(passphrase));
        Directory.CreateDirectory(directory);
        var identityPath = Path.Combine(directory, "receiver.identity");
        var pinPath = Path.Combine(directory, "receiver.pin.json");
        if (File.Exists(identityPath) != File.Exists(pinPath))
            throw new CryptographicException("Paired receiver identity or its public pin is missing; refusing to recreate it.");

        if (File.Exists(identityPath))
        {
            var envelope = JsonSerializer.Deserialize<PairedDongleIdentityEnvelope>(File.ReadAllText(identityPath), Json)
                           ?? throw new InvalidOperationException("Paired receiver identity is unreadable.");
            var pin = JsonSerializer.Deserialize<PairedDonglePin>(File.ReadAllText(pinPath), Json)
                      ?? throw new InvalidOperationException("Paired receiver public pin is unreadable.");
            if (envelope.V != 1 || envelope.Kdf != PinKdf.Id || envelope.Salt.Length != PinKdf.SaltSize ||
                envelope.Nonce.Length != Aead.NonceSize || envelope.Ciphertext.Length < Aead.TagSize ||
                pin.V != 1 || pin.Id != envelope.Id || pin.RsaPublicKeySpki != envelope.RsaPublicKeySpki)
                throw new CryptographicException("Paired receiver identity does not match its pinned public key.");

            var wrappingKey = PinKdf.Derive(passphrase, envelope.Salt);
            try
            {
                var privateBytes = Aead.Open(wrappingKey, envelope.Nonce, envelope.Ciphertext,
                    Utf8.B("kickl/v1/paired-identity|" + envelope.Id));
                try
                {
                    var privateKey = Convert.ToBase64String(privateBytes);
                    using var rsa = RSA.Create();
                    rsa.ImportPkcs8PrivateKey(privateBytes, out _);
                    if (Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()) != envelope.RsaPublicKeySpki)
                        throw new CryptographicException("Paired receiver private key does not match its public pin.");
                    return new PairedDongleIdentity { Id = envelope.Id,
                        RsaPublicKeySpki = envelope.RsaPublicKeySpki, RsaPrivateKeyPkcs8 = privateKey };
                }
                finally { CryptographicOperations.ZeroMemory(privateBytes); }
            }
            finally { CryptographicOperations.ZeroMemory(wrappingKey); }
        }

        var identity = CreatePairedIdentity();
        var salt = RandomNumberGenerator.GetBytes(PinKdf.SaltSize);
        var wrapping = PinKdf.Derive(passphrase, salt);
        var pkcs8 = Convert.FromBase64String(identity.RsaPrivateKeyPkcs8);
        try
        {
            var (nonce, ciphertext) = Aead.Seal(wrapping, pkcs8, Utf8.B("kickl/v1/paired-identity|" + identity.Id));
            var envelope = new PairedDongleIdentityEnvelope { Id = identity.Id,
                RsaPublicKeySpki = identity.RsaPublicKeySpki, Salt = salt, Nonce = nonce, Ciphertext = ciphertext };
            File.WriteAllText(identityPath, JsonSerializer.Serialize(envelope, Json));
            RestrictToOwner(identityPath);
            File.WriteAllText(pinPath, JsonSerializer.Serialize(new PairedDonglePin { Id = identity.Id,
                RsaPublicKeySpki = identity.RsaPublicKeySpki }, Json));
            RestrictToOwner(pinPath);
            return identity;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapping);
            CryptographicOperations.ZeroMemory(pkcs8);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public static PairedDonglePacket SealForPairedDongle(OwnershipProof proof, string recipientId, string recipientSpki)
    {
        var aesKey = RandomNumberGenerator.GetBytes(32);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(proof, Json);
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(recipientSpki), out _);
            var aad = Utf8.B($"kickl/v1/paired-proof|{proof.Site}|{recipientId}");
            var (nonce, ciphertext) = Aead.Seal(aesKey, plaintext, aad);
            return new PairedDonglePacket { Site = proof.Site, RecipientId = recipientId,
                WrappedAesKey = Convert.ToBase64String(rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256)),
                Nonce = nonce, Ciphertext = ciphertext };
        }
        finally { CryptographicOperations.ZeroMemory(aesKey); CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static OwnershipProof OpenAtPairedDongle(PairedDonglePacket packet, PairedDongleIdentity identity)
    {
        if (packet.V != 1 || packet.RecipientId != identity.Id || packet.Nonce.Length != Aead.NonceSize ||
            packet.Ciphertext.Length < Aead.TagSize)
            throw new CryptographicException("Paired-dongle packet is malformed or addressed to another receiver.");
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(identity.RsaPrivateKeyPkcs8), out _);
        var key = rsa.Decrypt(Convert.FromBase64String(packet.WrappedAesKey), RSAEncryptionPadding.OaepSHA256);
        try
        {
            var plaintext = Aead.Open(key, packet.Nonce, packet.Ciphertext,
                Utf8.B($"kickl/v1/paired-proof|{packet.Site}|{packet.RecipientId}"));
            try
            {
                var proof = JsonSerializer.Deserialize<OwnershipProof>(plaintext, Json)
                            ?? throw new CryptographicException("Paired proof payload is unreadable.");
                if (proof.Site != packet.Site) throw new CryptographicException("Paired proof site mismatch.");
                return proof;
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static PairedDongleAck CreatePairedDongleAck(PairedDongleIdentity identity,
        string site, string challenge, bool accepted)
    {
        var ack = new PairedDongleAck { ReceiverId = identity.Id, Site = site,
            Challenge = challenge, Accepted = accepted };
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(identity.RsaPrivateKeyPkcs8), out _);
        ack.Signature = Convert.ToBase64String(rsa.SignData(Utf8.B(PairedAckPayload(ack)),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        return ack;
    }

    public static bool VerifyPairedDongleAck(string pinnedPublicKeySpki, PairedDongleAck ack)
    {
        if (ack.V != 1 || string.IsNullOrWhiteSpace(ack.ReceiverId) ||
            string.IsNullOrWhiteSpace(ack.Site) || string.IsNullOrWhiteSpace(ack.Challenge) ||
            string.IsNullOrWhiteSpace(ack.Signature)) return false;
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pinnedPublicKeySpki), out _);
        return rsa.VerifyData(Utf8.B(PairedAckPayload(ack)), Convert.FromBase64String(ack.Signature),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    private static string PairedAckPayload(PairedDongleAck ack) =>
        $"kickl/v1/paired-ack|{ack.ReceiverId}|{ack.Site}|{ack.Challenge}|{ack.Accepted}";

    public static OwnershipPublic LoadPublic(DataPaths paths, string site) =>
        JsonSerializer.Deserialize<OwnershipPublic>(File.ReadAllText(PublicPath(paths, site)), Json)
        ?? throw new InvalidOperationException("The ownership record is unreadable.");

    public static OwnershipPublic LoadPublicFile(string path) =>
        JsonSerializer.Deserialize<OwnershipPublic>(File.ReadAllText(path), Json)
        ?? throw new InvalidOperationException("The owner public file is unreadable.");

    // --------------------------------------------------------------- challenge

    public static string NewChallenge() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string ProofPayload(string site, string challenge) =>
        $"kickl/v1/ownership-proof|{site}|{challenge}";

    /// <summary>Unwrap the AES token with the USB RSA key, sign the challenge, return the proof.</summary>
    public static OwnershipProof Prove(OwnerSecret secret, string challenge)
    {
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(secret.RsaPrivateKeyPkcs8), out _);

        var token = rsa.Decrypt(Convert.FromBase64String(secret.WrappedKey), RSAEncryptionPadding.OaepSHA256);
        try
        {
            var tokenBytes = Aead.Open(token, secret.TokenNonce, secret.TokenCiphertext,
                Utf8.B("kickl/v1/owner-token|" + secret.Site));
            try
            {
                return new OwnershipProof
                {
                    Site = secret.Site,
                    Challenge = challenge,
                    Token = Convert.ToBase64String(tokenBytes),
                    RsaSignature = Convert.ToBase64String(
                        rsa.SignData(Utf8.B(ProofPayload(secret.Site, challenge)),
                            HashAlgorithmName.SHA256, RSASignaturePadding.Pss)),
                    CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tokenBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    public static bool VerifyProof(OwnershipPublic pub, OwnershipProof proof)
    {
        if (!string.Equals(proof.Site, pub.Site, StringComparison.OrdinalIgnoreCase)) return false;
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(TokenCommitmentFor(proof.Token)),
                Convert.FromHexString(pub.TokenCommitment)))
            return false;

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pub.RsaPublicKeySpki), out _);
        return rsa.VerifyData(Utf8.B(ProofPayload(pub.Site, proof.Challenge)),
            Convert.FromBase64String(proof.RsaSignature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    public static TitleRecord Issue(DataPaths paths, OwnershipPublic pub, OwnershipProof proof)
    {
        if (!VerifyProof(pub, proof))
            throw new CryptographicException("Ownership proof failed: wrong token or wrong USB key.");

        var now = DateTimeOffset.UtcNow;
        var title = new TitleRecord
        {
            Site = pub.Site,
            OwnerId = pub.OwnerId,
            RsaPublicKeySpki = pub.RsaPublicKeySpki,
            TokenCommitment = pub.TokenCommitment,
            BadgeId = BadgeIdFor(pub.Site, pub.OwnerId, 1, now),
            Seq = 1,
            Status = "active",
            IssuedUtc = now.ToString("O"),
            ExpiresUtc = now.AddYears(1).ToString("O")
        };

        SaveTitle(paths, title);
        ApplyToRegistry(paths, title);
        return title;
    }

    // ------------------------------------------------------------------ transfer

    private static string TransferPayload(TransferStatement s) =>
        $"kickl/v1/ownership-transfer|{s.Site}|{s.FromOwnerId}|{s.ToOwnerId}|" +
        $"{s.ToRsaPublicKeySpki}|{s.ToTokenCommitment}|{s.Seq}|{s.Price}|{s.CreatedUtc}";

    public static TransferStatement CreateTransfer(
        DataPaths paths, TitleRecord current, OwnerSecret seller, OwnershipPublic buyer, string price)
    {
        if (!string.Equals(current.RsaPublicKeySpki, seller.Public.RsaPublicKeySpki, StringComparison.Ordinal))
            throw new InvalidOperationException("The USB key is not the current title holder.");
        if (current.Status is not ("active" or "listed"))
            throw new InvalidOperationException($"Title status '{current.Status}' cannot be transferred.");
        if (!string.Equals(buyer.Site, current.Site, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The buyer record is for a different name.");

        var registry = LocalRegistry.LoadOrEmpty(paths);
        var registryRecord = LocalRegistry.Resolve(registry, current.Site);
        if (registryRecord is null || registryRecord.TitleSeq != current.Seq ||
            !string.Equals(registryRecord.BadgeId, current.BadgeId, StringComparison.Ordinal))
            throw new InvalidOperationException("This title is stale; refresh it from the registry before selling.");

        var statement = new TransferStatement
        {
            Site = current.Site,
            FromOwnerId = current.OwnerId,
            ToOwnerId = buyer.OwnerId,
            ToRsaPublicKeySpki = buyer.RsaPublicKeySpki,
            ToTokenCommitment = buyer.TokenCommitment,
            Seq = current.Seq + 1,
            Price = price,
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
        };

        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(seller.RsaPrivateKeyPkcs8), out _);
        statement.SellerSignature = Convert.ToBase64String(
            rsa.SignData(Utf8.B(TransferPayload(statement)), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        return statement;
    }

    public static TitleRecord AcceptTransfer(
        DataPaths paths, TransferStatement statement, OwnershipPublic buyer, OwnershipProof buyerProof)
    {
        var current = LoadTitle(paths, statement.Site)
                      ?? throw new InvalidOperationException("There is no title to transfer.");

        if (current.Status is "closed" or "transferred")
            throw new InvalidOperationException($"Title status '{current.Status}' is final.");
        if (statement.Seq != current.Seq + 1)
            throw new InvalidOperationException("Transfer sequence is stale or skips a title.");
        if (!string.Equals(statement.FromOwnerId, current.OwnerId, StringComparison.Ordinal))
            throw new InvalidOperationException("The transfer does not name the current owner.");
        if (!string.Equals(statement.ToOwnerId, buyer.OwnerId, StringComparison.Ordinal) ||
            !string.Equals(statement.ToRsaPublicKeySpki, buyer.RsaPublicKeySpki, StringComparison.Ordinal) ||
            !string.Equals(statement.ToTokenCommitment, buyer.TokenCommitment, StringComparison.Ordinal))
            throw new InvalidOperationException("The buyer key does not match the transfer.");
        if (!VerifyProof(buyer, buyerProof))
            throw new CryptographicException("The buyer did not prove possession of the new key.");

        using var seller = RSA.Create();
        seller.ImportSubjectPublicKeyInfo(Convert.FromBase64String(current.RsaPublicKeySpki), out _);
        if (!seller.VerifyData(Utf8.B(TransferPayload(statement)),
                Convert.FromBase64String(statement.SellerSignature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("The seller signature is invalid.");

        var now = DateTimeOffset.UtcNow;
        var title = new TitleRecord
        {
            Site = current.Site,
            OwnerId = buyer.OwnerId,
            RsaPublicKeySpki = buyer.RsaPublicKeySpki,
            TokenCommitment = buyer.TokenCommitment,
            BadgeId = BadgeIdFor(current.Site, buyer.OwnerId, statement.Seq, now),
            Seq = statement.Seq,
            Status = "active",
            Tier = current.Tier,
            PaymentRef = current.PaymentRef,
            IssuedUtc = now.ToString("O"),
            ExpiresUtc = now.AddYears(1).ToString("O")
        };

        SaveTitle(paths, title);
        ApplyToRegistry(paths, title);
        return title;
    }

    public static void List(DataPaths paths, TitleRecord title, bool listed)
    {
        title.Status = listed ? "listed" : "active";
        SaveTitle(paths, title);
        ApplyToRegistry(paths, title);
    }

    // ------------------------------------------------------------------- storage

    public static void SaveTitle(DataPaths paths, TitleRecord title)
    {
        Directory.CreateDirectory(SharedDir(paths));
        File.WriteAllText(TitlePath(paths, title.Site), JsonSerializer.Serialize(title, Json));
    }

    public static TitleRecord? LoadTitle(DataPaths paths, string site)
    {
        var path = TitlePath(paths, site);
        return File.Exists(path) ? JsonSerializer.Deserialize<TitleRecord>(File.ReadAllText(path), Json) : null;
    }

    public static void ApplyToRegistry(DataPaths paths, TitleRecord title)
    {
        var registry = LocalRegistry.LoadOrEmpty(paths);
        var record = LocalRegistry.Resolve(registry, title.Site);
        if (record is null)
        {
            record = new RegistryRecord { Name = title.Site, DocFile = "", Status = "active" };
            registry.Records.Add(record);
            registry.Records.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }
        record.OwnerId = title.OwnerId;
        record.BadgeId = title.BadgeId;
        record.TitleSeq = title.Seq;
        record.Status = "active";
        LocalRegistry.Save(paths, registry);
    }

    public static List<TitleRecord> History(DataPaths paths)
    {
        var dir = SharedDir(paths);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.title.json")
            .Select(f => JsonSerializer.Deserialize<TitleRecord>(File.ReadAllText(f), Json))
            .Where(t => t is not null)
            .Cast<TitleRecord>()
            .OrderBy(t => t.Site, StringComparer.Ordinal)
            .ToList();
    }

    // -------------------------------------------------------------------- helpers

    private static string TokenCommitmentFor(string tokenB64) =>
        Convert.ToHexString(SHA256.HashData(Utf8.B("kickl/v1/token|" + tokenB64))).ToLowerInvariant();

    private static string OwnerIdFor(byte[] spki) =>
        "kb1" + Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant()[..20];

    private static string BadgeIdFor(string site, string ownerId, int seq, DateTimeOffset now) =>
        "kb1b" + Convert.ToHexString(SHA256.HashData(Utf8.B($"{site}|{ownerId}|{seq}|{now:O}")))
            .ToLowerInvariant()[..16];

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (IOException) { /* filesystem without modes */ }
    }
}

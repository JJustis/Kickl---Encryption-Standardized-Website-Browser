using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kickl.Core.Crypto;
using Kickl.Core.Util;

namespace Kickl.Core.Mail;

/// <summary>
/// The public mail identity. Dilithium (ML-DSA-65) authenticates the bundle and every
/// envelope; RSA-3072 is used only to hand the per-message AES-256 key to the recipient.
/// The short id is the number a person shares (<c>4821 7390</c>); the long id is routing.
/// </summary>
public sealed class MailIdentityBundle
{
    public int V { get; set; } = 1;
    public string Id { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string Handle { get; set; } = "";
    /// <summary>Direct HTTPS/HTTP endpoint where this device accepts sealed mail.</summary>
    public string Endpoint { get; set; } = "";
    public string RsaPublicKey { get; set; } = "";
    public string MlDsaPublicKey { get; set; } = "";
    public long CreatedAt { get; set; }
    public string Sig { get; set; } = "";

    public byte[] CanonicalBytes()
    {
        ValidateShape();
        return Encoding.UTF8.GetBytes(
            $"kickl/v1/mail-identity|{V}|{Id}|{ShortId}|{Handle}|{Endpoint}|" +
            $"{RsaPublicKey}|{MlDsaPublicKey}|{CreatedAt}");
    }

    public string Fingerprint => Hashing.Hex(SHA256.HashData(CanonicalBytes()));

    public bool VerifySignature()
    {
        try
        {
            var publicKey = B64u.Decode(MlDsaPublicKey);
            using var mlDsa = MLDsa.ImportMLDsaPublicKey(MLDsaAlgorithm.MLDsa65, publicKey);
            return mlDsa.VerifyData(CanonicalBytes(), B64u.Decode(Sig));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException
                                       or ArgumentException)
        {
            return false;
        }
    }

    public void Validate()
    {
        ValidateShape();
        if (Sig.Length == 0 || !VerifySignature())
            throw new CryptographicException("The mail identity signature did not verify.");
        if (B64u.Decode(RsaPublicKey).Length < 256)
            throw new FormatException("The mail RSA key is too short.");
        if (B64u.Decode(MlDsaPublicKey).Length != 1952)
            throw new FormatException("The mail Dilithium key is the wrong size.");
    }

    private void ValidateShape()
    {
        if (V != 1) throw new FormatException("Unsupported mail identity version.");
        if (!IsHexId(Id)) throw new FormatException("The mail identity id is malformed.");
        if (ShortId.Length != 8 || !ShortId.All(char.IsAsciiDigit))
            throw new FormatException("The mail short id must be eight digits.");
        if (string.IsNullOrWhiteSpace(Handle) || Handle.Length > 32 || Handle.Any(char.IsControl))
            throw new FormatException("The mail handle must be 1-32 printable characters.");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(endpoint.Host))
            throw new FormatException("The mail endpoint must be an absolute http(s) URL.");
        if (string.IsNullOrEmpty(RsaPublicKey) || string.IsNullOrEmpty(MlDsaPublicKey))
            throw new FormatException("The mail identity is missing a public key.");
    }

    private static bool IsHexId(string value) =>
        value.Length is >= 16 and <= 64 && value.All(Uri.IsHexDigit);

    public static MailIdentityBundle Parse(string json)
    {
        var bundle = JsonSerializer.Deserialize<MailIdentityBundle>(json, MailJson.Options)
                     ?? throw new FormatException("The mail identity is empty.");
        bundle.Validate();
        return bundle;
    }

    public string ToJson() => JsonSerializer.Serialize(this, MailJson.Options);
}

public sealed class MailEnvelope
{
    public int V { get; set; } = 1;
    public string Id { get; set; } = "";
    public string ToShortId { get; set; } = "";
    public string FromId { get; set; } = "";
    public string FromShortId { get; set; } = "";
    public string FromHandle { get; set; } = "";
    public long CreatedAt { get; set; }
    public string WrappedKey { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string ImageHash { get; set; } = "";
    public MailIdentityBundle SenderBundle { get; set; } = new();
    public string Sig { get; set; } = "";

    public string ToJson() => JsonSerializer.Serialize(this, MailJson.Options);

    public static MailEnvelope Parse(string json) =>
        JsonSerializer.Deserialize<MailEnvelope>(json, MailJson.Options)
        ?? throw new FormatException("The mail envelope is empty.");
}

public sealed record MailContent(string Subject, string Body);

public sealed record SealedMail(MailEnvelope Envelope, byte[] Image, byte[] MessageKey);

public sealed record OpenedMail(MailContent Content, byte[] MessageKey);

/// <summary>
/// Dilithium (ML-DSA-65) + RSA-3072 + AES-256-GCM mail. A fresh AES key is generated
/// for every message; RSA-OAEP wraps it for the recipient; AES-GCM seals the Brotli
/// payload; the sender signs the envelope with Dilithium. The payload travels inside a
/// PNG private chunk, never as readable text or base64.
/// </summary>
public static class MailCrypto
{
    public const int MaxBodyChars = 90_000;
    public const int MaxSubjectChars = 200;
    public const int MaxImageBytes = 4 * 1024 * 1024;
    public const int MessageKeySize = 32;
    private const string EnvelopeDomain = "kickl/v1/mail-envelope";

    public static SealedMail Seal(MailIdentity sender, MailIdentityBundle recipient,
        string subject, string body, byte[]? coverPng = null)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(recipient);
        if (!recipient.VerifySignature())
            throw new CryptographicException("The recipient mail identity signature did not verify.");
        if (subject.Length > MaxSubjectChars)
            throw new ArgumentException($"Subjects are limited to {MaxSubjectChars} characters.", nameof(subject));
        if (body.Length > MaxBodyChars)
            throw new ArgumentException($"Messages are limited to {MaxBodyChars} characters.", nameof(body));

        var id = Guid.NewGuid().ToString("N");
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new MailPayload
        {
            V = 1,
            Subject = subject,
            Body = body
        }, MailJson.Options);
        var compressed = Compress(payload);
        CryptographicOperations.ZeroMemory(payload);

        var messageKey = RandomNumberGenerator.GetBytes(MessageKeySize);
        try
        {
            var aad = MessageAad(id, recipient.ShortId, sender.Bundle.Id);
            var (nonce, ciphertext) = Aead.Seal(messageKey, compressed, aad);
            CryptographicOperations.ZeroMemory(compressed);

            var image = MailImage.Attach(coverPng ?? MailImage.CreateCover(), ciphertext);
            if (image.Length > MaxImageBytes)
                throw new InvalidOperationException("The sealed mail image is too large.");

            using var recipientRsa = RSA.Create();
            recipientRsa.ImportSubjectPublicKeyInfo(B64u.Decode(recipient.RsaPublicKey), out _);
            var wrapped = recipientRsa.Encrypt(messageKey, RSAEncryptionPadding.OaepSHA256);

            var envelope = new MailEnvelope
            {
                Id = id,
                ToShortId = recipient.ShortId,
                FromId = sender.Bundle.Id,
                FromShortId = sender.Bundle.ShortId,
                FromHandle = sender.Bundle.Handle,
                CreatedAt = createdAt,
                WrappedKey = B64u.Encode(wrapped),
                Nonce = B64u.Encode(nonce),
                ImageHash = Hashing.Hex(SHA256.HashData(image)),
                SenderBundle = sender.Bundle
            };
            envelope.Sig = sender.Sign(EnvelopeCanonical(envelope));
            return new SealedMail(envelope, image, messageKey);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(messageKey);
            throw;
        }
    }

    /// <summary>Verify the sender, unwrap the AES key and open a received message.</summary>
    public static OpenedMail Open(MailEnvelope envelope, byte[] image, RSA recipientRsa,
        string expectedShortId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(recipientRsa);

        if (envelope.V != 1) throw new FormatException("Unsupported mail envelope version.");
        if (!string.Equals(envelope.ToShortId, expectedShortId, StringComparison.Ordinal))
            throw new CryptographicException("This message was addressed to another mail id.");
        if (!string.Equals(envelope.SenderBundle.ShortId, envelope.FromShortId, StringComparison.Ordinal)
            || !string.Equals(envelope.SenderBundle.Id, envelope.FromId, StringComparison.Ordinal)
            || !string.Equals(envelope.SenderBundle.Handle, envelope.FromHandle, StringComparison.Ordinal))
            throw new CryptographicException("The sender fields do not match the signed identity.");
        envelope.SenderBundle.Validate();
        if (!envelope.SenderBundle.VerifySignature())
            throw new CryptographicException("The sender identity is not self-signed.");

        var imageHash = Hashing.Hex(SHA256.HashData(image));
        if (!string.Equals(imageHash, envelope.ImageHash, StringComparison.Ordinal))
            throw new CryptographicException("The mail image was modified in transit.");

        using var signer = MLDsa.ImportMLDsaPublicKey(MLDsaAlgorithm.MLDsa65,
            B64u.Decode(envelope.SenderBundle.MlDsaPublicKey));
        if (!signer.VerifyData(EnvelopeCanonical(envelope), B64u.Decode(envelope.Sig)))
            throw new CryptographicException("The mail envelope signature did not verify.");

        var key = recipientRsa.Decrypt(B64u.Decode(envelope.WrappedKey),
            RSAEncryptionPadding.OaepSHA256);
        if (key.Length != MessageKeySize)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("The wrapped mail key has the wrong size.");
        }

        try
        {
            return new OpenedMail(OpenWithKey(key, image, envelope), key);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    public static MailContent OpenWithKey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> image,
        MailEnvelope envelope)
    {
        var sealedBytes = MailImage.Extract(image);
        var aad = MessageAad(envelope.Id, envelope.ToShortId, envelope.FromId);
        byte[] compressed;
        try
        {
            compressed = Aead.Open(key, B64u.Decode(envelope.Nonce), sealedBytes, aad);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("The mail password is wrong or the message was modified.");
        }

        try
        {
            var payload = JsonSerializer.Deserialize<MailPayload>(Decompress(compressed), MailJson.Options)
                          ?? throw new CryptographicException("The message is unreadable.");
            if (payload.V != 1) throw new CryptographicException("Unsupported message version.");
            if (payload.Body.Length > MaxBodyChars)
                throw new CryptographicException("The message exceeds the 90,000 character limit.");
            return new MailContent(payload.Subject, payload.Body);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(compressed);
        }
    }

    public static (byte[] Nonce, byte[] Ciphertext) SealLocalKey(ReadOnlySpan<byte> masterKey,
        string recordId, ReadOnlySpan<byte> messageKey)
    {
        var aad = LocalKeyAad(recordId);
        return Aead.Seal(masterKey, messageKey, aad);
    }

    public static byte[] OpenLocalKey(ReadOnlySpan<byte> masterKey, string recordId,
        byte[] nonce, byte[] ciphertext)
    {
        var key = Aead.Open(masterKey, nonce, ciphertext, LocalKeyAad(recordId));
        if (key.Length != MessageKeySize)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("The local mail key is malformed.");
        }
        return key;
    }

    public static byte[] EnvelopeCanonical(MailEnvelope envelope) =>
        Encoding.UTF8.GetBytes(
            $"{EnvelopeDomain}|{envelope.V}|{envelope.Id}|{envelope.ToShortId}|{envelope.FromId}|" +
            $"{envelope.FromShortId}|{envelope.FromHandle}|{envelope.CreatedAt}|" +
            $"{envelope.WrappedKey}|{envelope.Nonce}|{envelope.ImageHash}|" +
            $"{envelope.SenderBundle.Fingerprint}");

    private static byte[] MessageAad(string id, string toShortId, string fromId) =>
        Encoding.UTF8.GetBytes($"kickl/v1/mail-message|{id}|{toShortId}|{fromId}|1");

    private static byte[] LocalKeyAad(string recordId) =>
        Encoding.UTF8.GetBytes($"kickl/v1/mail-local|{recordId}|1");

    private static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(data);
        return output.ToArray();
    }

    private static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray(), writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > 4 * 1024 * 1024)
                throw new CryptographicException("The decompressed message is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private sealed class MailPayload
    {
        public int V { get; set; } = 1;
        public string Subject { get; set; } = "";
        public string Body { get; set; } = "";
    }
}

internal static class MailJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>The live private identity for one profile. Dispose clears the key handles.</summary>
public sealed class MailIdentity : IDisposable
{
    internal MailIdentity(RSA rsa, MLDsa mlDsa, MailIdentityBundle bundle, string token,
        byte[] rsaPkcs8, byte[] mlDsaPkcs8, System.Security.Cryptography.X509Certificates.X509Certificate2 tls)
    {
        Rsa = rsa;
        MlDsa = mlDsa;
        Bundle = bundle;
        Token = token;
        RsaPkcs8 = rsaPkcs8;
        MlDsaPkcs8 = mlDsaPkcs8;
        TlsCertificate = tls;
    }

    /// <summary>This device's TLS certificate for the direct-delivery listener. Only its key is trusted, through the pin.</summary>
    public System.Security.Cryptography.X509Certificates.X509Certificate2 TlsCertificate { get; }
    public string TlsPin => MailTls.PinOf(TlsCertificate);
    public byte[] TlsPkcs12 => MailTls.Export(TlsCertificate);

    /// <summary>True when loading this identity had to change its published endpoint (it was unpinned or plain http), so it must be republished.</summary>
    public bool TransportUpgraded { get; private set; }

    /// <summary>Publish <paramref name="endpoint"/> as https with this device's pin, and re-sign the bundle so the pin is authenticated.</summary>
    public void SetEndpoint(string endpoint)
    {
        Bundle.Endpoint = MailEndpoint.WithPin(endpoint, TlsPin);
        Bundle.Sig = Sign(Bundle.CanonicalBytes());
    }

    public RSA Rsa { get; }
    public MLDsa MlDsa { get; }
    public MailIdentityBundle Bundle { get; }
    public string Token { get; }
    public byte[] RsaPkcs8 { get; }
    public byte[] MlDsaPkcs8 { get; }

    public string Sign(byte[] payload) => B64u.Encode(MlDsa.SignData(payload));

    public static MailIdentity Generate(string handle, string? endpoint = null)
    {
        if (!MLDsa.IsSupported)
            throw new PlatformNotSupportedException(
                "ML-DSA-65 is unavailable on this platform; mail requires Dilithium.");

        var shortId = RandomNumberGenerator.GetInt32(10_000_000, 100_000_000).ToString();
        var id = Hashing.Hex(RandomNumberGenerator.GetBytes(12));
        var token = B64u.Encode(RandomNumberGenerator.GetBytes(32));
        var tls = MailTls.CreateSelfSigned();
        var rsa = RSA.Create(3072);
        var mlDsa = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa65);
        var rsaPkcs8 = rsa.ExportPkcs8PrivateKey();
        var mlDsaPkcs8 = mlDsa.ExportPkcs8PrivateKey();
        var bundle = new MailIdentityBundle
        {
            Id = id,
            ShortId = shortId,
            Handle = handle.Trim(),
            Endpoint = MailEndpoint.WithPin(endpoint ?? MailEndpoint.LocalBase(), MailTls.PinOf(tls)),
            RsaPublicKey = B64u.Encode(rsa.ExportSubjectPublicKeyInfo()),
            MlDsaPublicKey = B64u.Encode(mlDsa.ExportMLDsaPublicKey()),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        bundle.Sig = B64u.Encode(mlDsa.SignData(bundle.CanonicalBytes()));
        return new MailIdentity(rsa, mlDsa, bundle, token, rsaPkcs8, mlDsaPkcs8, tls);
    }

    public static MailIdentity Restore(MailIdentityBundle bundle, string token,
        byte[] rsaPkcs8, byte[] mlDsaPkcs8, byte[]? tlsPkcs12 = null)
    {
        bundle.Validate();
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(rsaPkcs8, out _);
        var mlDsa = MLDsa.ImportPkcs8PrivateKey(mlDsaPkcs8);

        // An identity made before mail was TLS-only has no transport key. Give it one.
        var hadKey = tlsPkcs12 is { Length: > 0 };
        var tls = hadKey ? MailTls.Import(tlsPkcs12!) : MailTls.CreateSelfSigned();
        var identity = new MailIdentity(rsa, mlDsa, bundle, token, rsaPkcs8, mlDsaPkcs8, tls);

        // ...and its published endpoint must be https with exactly that key's pin. Keep the host and port; fix the rest.
        string wanted;
        try { wanted = MailEndpoint.WithPin(bundle.Endpoint, identity.TlsPin); }
        catch (ArgumentException) { wanted = MailEndpoint.WithPin(MailEndpoint.LocalBase(), identity.TlsPin); }
        if (!hadKey || !string.Equals(wanted, bundle.Endpoint, StringComparison.Ordinal))
        {
            identity.SetEndpoint(wanted);
            identity.TransportUpgraded = true;
        }
        return identity;
    }

    public void Dispose()
    {
        Rsa.Dispose();
        MlDsa.Dispose();
        TlsCertificate.Dispose();
        CryptographicOperations.ZeroMemory(RsaPkcs8);
        CryptographicOperations.ZeroMemory(MlDsaPkcs8);
    }
}

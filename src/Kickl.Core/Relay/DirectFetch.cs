using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kickl.Core.Crypto;
using Kickl.Core.Documents;
using Kickl.Core.Names;
using Kickl.Core.Util;

namespace Kickl.Core.Relay;

public sealed record DirectPeer(string DeviceId, byte[] PublicKeySpki);

public sealed class DirectFetchRequest
{
    public string RequestId { get; set; } = "";
    public byte[] WrappedKey { get; set; } = [];
    public byte[] Salt { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public long CreatedUtc { get; set; }
}

public sealed class DirectFetchResponse
{
    public string RequestId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Signature { get; set; } = [];
    public long CreatedUtc { get; set; }
}

public sealed class DirectFetchException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed record DirectFetchResult(SealedDocument Document, string Transport, string Fingerprint);

/// <summary>
/// Direct article delivery from the origin device. The reader pins the origin's RSA public key
/// from the registry, RSA-OAEP wraps a fresh 32-byte AES seed, HKDF splits request/response keys,
/// and both frames are AES-256-GCM. The response is signed with RSA-PSS. The registry hash is
/// still checked before parsing, so the tunnel cannot substitute content.
/// </summary>
public static class DirectFetchClient
{
    public static DirectFetchExchange Begin(DirectPeer peer, string host)
    {
        if (peer.PublicKeySpki.Length == 0)
            throw new DirectFetchException("The registry has no direct-fetch key for this origin.");
        if (!JttpUrl.TryParse("jttp://" + host + "/", out _, out var error))
            throw new DirectFetchException(error ?? "Invalid host.");

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(peer.PublicKeySpki, out _);
        }
        catch (CryptographicException ex)
        {
            throw new DirectFetchException("The origin's direct-fetch key is malformed.", ex);
        }

        var requestId = B64u.Encode(RandomNumberGenerator.GetBytes(16));
        var salt = RandomNumberGenerator.GetBytes(16);
        var tunnel = RandomNumberGenerator.GetBytes(Aead.KeySize);
        var wrapped = rsa.Encrypt(tunnel, RSAEncryptionPadding.OaepSHA256);
        var requestKey = Hkdf.Derive(tunnel, salt, "kickl/v1/direct/request");
        var responseKey = Hkdf.Derive(tunnel, salt, "kickl/v1/direct/response");

        var payload = new DirectRequestPayload
        {
            RequestId = requestId,
            Host = host,
            CreatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var (nonce, ciphertext) = Aead.Seal(requestKey, json,
            Aad(requestId, "request"));
        CryptographicOperations.ZeroMemory(json);
        CryptographicOperations.ZeroMemory(tunnel);

        var request = new DirectFetchRequest
        {
            RequestId = requestId,
            WrappedKey = wrapped,
            Salt = salt,
            Nonce = nonce,
            Ciphertext = ciphertext,
            CreatedUtc = payload.CreatedUtc
        };
        return new DirectFetchExchange(peer, host, requestKey, responseKey, request);
    }

    internal static byte[] Aad(string requestId, string direction) =>
        Utf8.B($"kickl/v1/direct|{requestId}|{direction}");

    internal static byte[] SigningBytes(string host, string integrity, string requestId,
        long createdUtc, string deviceId) =>
        Utf8.B($"kickl/v1/direct-sign|{host}|{integrity}|{requestId}|{createdUtc}|{deviceId}");

    internal sealed record DirectRequestPayload
    {
        public string RequestId { get; init; } = "";
        public string Host { get; init; } = "";
        public long CreatedUtc { get; init; }
    }

    internal sealed record DirectResponsePayload
    {
        public string RequestId { get; init; } = "";
        public string DeviceId { get; init; } = "";
        public long CreatedUtc { get; init; }
        public SealedDocument Document { get; init; } = new();
    }
}

public sealed class DirectFetchExchange : IDisposable
{
    private readonly DirectPeer _peer;
    private readonly string _host;
    private readonly byte[] _requestKey;
    private readonly byte[] _responseKey;

    internal DirectFetchExchange(DirectPeer peer, string host,
        byte[] requestKey, byte[] responseKey, DirectFetchRequest request)
    {
        _peer = peer;
        _host = host;
        _requestKey = requestKey;
        _responseKey = responseKey;
        Request = request;
    }

    public DirectFetchRequest Request { get; }

    public DirectFetchResult Complete(DirectFetchResponse response)
    {
        if (!string.Equals(response.RequestId, Request.RequestId, StringComparison.Ordinal))
            throw new DirectFetchException("The direct-fetch response belongs to another request.");
        if (response.Ciphertext.Length > DocumentStore.MaxDocumentBytes)
            throw new DirectFetchException("The direct-fetch response is larger than any site package may be.");
        if (response.Ciphertext.Length == 0 || response.Nonce.Length != Aead.NonceSize)
            throw new DirectFetchException("The direct-fetch response is incomplete.");

        byte[] plain;
        try
        {
            plain = Aead.Open(_responseKey, response.Nonce, response.Ciphertext,
                DirectFetchClient.Aad(Request.RequestId, "response"));
        }
        catch (CryptographicException ex)
        {
            throw new DirectFetchException("The direct-fetch response failed authentication.", ex);
        }

        DirectFetchClient.DirectResponsePayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<DirectFetchClient.DirectResponsePayload>(plain)
                      ?? throw new DirectFetchException("The direct-fetch response is unreadable.");
        }
        catch (JsonException ex)
        {
            throw new DirectFetchException("The direct-fetch response is unreadable.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        if (!string.Equals(payload.RequestId, Request.RequestId, StringComparison.Ordinal))
            throw new DirectFetchException("The direct-fetch response request id does not match.");
        if (!string.Equals(payload.DeviceId, _peer.DeviceId, StringComparison.Ordinal))
            throw new DirectFetchException("The response came from an unexpected device.");
        if (!string.Equals(payload.Document.Site, _host, StringComparison.OrdinalIgnoreCase))
            throw new DirectFetchException("The origin returned a document for another name.");

        using (var rsa = RSA.Create())
        {
            rsa.ImportSubjectPublicKeyInfo(_peer.PublicKeySpki, out _);
            var signed = DirectFetchClient.SigningBytes(_host, payload.Document.Integrity,
                Request.RequestId, response.CreatedUtc, payload.DeviceId);
            var ok = rsa.VerifyData(signed, response.Signature, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
            if (!ok) throw new DirectFetchException("The origin's direct-fetch signature is invalid.");
        }

        var recomputed = DocumentStore.ComputeIntegrity(payload.Document);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(recomputed), Convert.FromHexString(payload.Document.Integrity)))
            throw new DirectFetchException("The delivered package failed its own integrity check.");

        var fingerprint = Convert.ToHexString(SHA256.HashData(
            _peer.PublicKeySpki.Concat(Encoding.UTF8.GetBytes(Request.RequestId)).ToArray()))
            .ToLowerInvariant()[..16];

        return new DirectFetchResult(payload.Document,
            "direct origin device · RSA-OAEP + AES-256-GCM", fingerprint);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_requestKey);
        CryptographicOperations.ZeroMemory(_responseKey);
    }
}

/// <summary>The origin side. It holds the private RSA key and serves the sealed package.</summary>
public sealed class DirectPublisher : IDisposable
{
    private readonly RelayIdentity _identity;
    private readonly Func<string, SealedDocument> _load;

    private DirectPublisher(RelayIdentity identity, Func<string, SealedDocument> load)
    {
        _identity = identity;
        _load = load;
    }

    public static DirectPublisher Open(DataPaths paths, byte[] master)
    {
        var identity = DeviceRelayKey.Ensure(paths, master);
        return new DirectPublisher(identity, host => DocumentStore.Load(paths, host));
    }

    public string DeviceId => _identity.DeviceId;

    public DirectFetchResponse Respond(DirectFetchRequest request)
    {
        if (request.WrappedKey.Length == 0 || request.Salt.Length != 16 ||
            request.Nonce.Length != Aead.NonceSize || request.Ciphertext.Length == 0)
            throw new DirectFetchException("The direct-fetch request is incomplete.");

        byte[] tunnel;
        try
        {
            tunnel = _identity.Rsa.Decrypt(request.WrappedKey, RSAEncryptionPadding.OaepSHA256);
        }
        catch (CryptographicException ex)
        {
            throw new DirectFetchException("The direct-fetch key wrap failed.", ex);
        }

        var requestKey = Hkdf.Derive(tunnel, request.Salt, "kickl/v1/direct/request");
        var responseKey = Hkdf.Derive(tunnel, request.Salt, "kickl/v1/direct/response");
        CryptographicOperations.ZeroMemory(tunnel);

        if (request.Ciphertext.Length > 64 * 1024)   // a request names one site; it is never large
            throw new DirectFetchException("The direct-fetch request is larger than any request may be.");

        byte[] plain;
        try
        {
            plain = Aead.Open(requestKey, request.Nonce, request.Ciphertext,
                DirectFetchClient.Aad(request.RequestId, "request"));
        }
        catch (CryptographicException ex)
        {
            throw new DirectFetchException("The direct-fetch request failed authentication.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(requestKey);
        }

        DirectFetchClient.DirectRequestPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<DirectFetchClient.DirectRequestPayload>(plain)
                      ?? throw new DirectFetchException("The direct-fetch request is unreadable.");
        }
        catch (JsonException ex)
        {
            throw new DirectFetchException("The direct-fetch request is unreadable.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        if (!string.Equals(payload.RequestId, request.RequestId, StringComparison.Ordinal))
            throw new DirectFetchException("The request id does not match.");
        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - payload.CreatedUtc) > 300)
            throw new DirectFetchException("The direct-fetch request is stale.");
        if (!JttpUrl.TryParse("jttp://" + payload.Host + "/", out _, out var error))
            throw new DirectFetchException(error ?? "Invalid host.");

        var document = _load(payload.Host);
        if (!string.Equals(document.Site, payload.Host, StringComparison.OrdinalIgnoreCase))
            throw new DirectFetchException("The origin document does not match the requested name.");

        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var responsePayload = new DirectFetchClient.DirectResponsePayload
        {
            RequestId = payload.RequestId,
            DeviceId = _identity.DeviceId,
            CreatedUtc = created,
            Document = document
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(responsePayload);
        var (nonce, ciphertext) = Aead.Seal(responseKey, json,
            DirectFetchClient.Aad(request.RequestId, "response"));
        CryptographicOperations.ZeroMemory(json);
        CryptographicOperations.ZeroMemory(responseKey);

        var signed = DirectFetchClient.SigningBytes(payload.Host, document.Integrity,
            payload.RequestId, created, _identity.DeviceId);
        var signature = _identity.Rsa.SignData(signed, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);

        return new DirectFetchResponse
        {
            RequestId = payload.RequestId,
            DeviceId = _identity.DeviceId,
            Nonce = nonce,
            Ciphertext = ciphertext,
            Signature = signature,
            CreatedUtc = created
        };
    }

    public void Dispose() => _identity.Dispose();
}

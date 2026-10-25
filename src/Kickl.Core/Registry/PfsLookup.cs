using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kickl.Core.Crypto;
using Kickl.Core.Util;

namespace Kickl.Core.Registry;

/// <summary>
/// PFS name lookup, ported from LegendDX's working implementation
/// (C:/xampp/htdocs/legenddx/pfs/server.php + pfs/client.js):
///   - fresh P-256 ECDH key pair per session on both sides
///   - raw ECDH secret → HKDF-SHA384 (16-byte salt) → 32-byte AES key
///   - AES-256-GCM with a 12-byte nonce
///   - session keys live in memory, one-hour cap, invalidated explicitly
/// The registry name is inside the ciphertext, so an observer sees neither the query nor
/// whether the name exists. The key is destroyed after the lookup.
/// </summary>
public sealed class PfsServerHello
{
    public string SessionId { get; set; } = "";
    public byte[] ServerPublicKeySpki { get; set; } = [];
    public byte[] Salt { get; set; } = [];
}

public sealed class PfsSealed
{
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
}

public sealed class LookupResponse
{
    public bool Found { get; set; }
    public string Host { get; set; } = "";
    public string Status { get; set; } = "closed";
    public string DocFile { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string BadgeId { get; set; } = "";
    public int Version { get; set; }
    public string Title { get; set; } = "";
    public string Headline { get; set; } = "";
    public string StoryAuthor { get; set; } = "";
    public string StoryDate { get; set; } = "";
    public string StoryLocation { get; set; } = "";
    public string[] Tags { get; set; } = [];
    public long UpdatedUtc { get; set; }
    /// <summary>SHA-256 of the delivered package. Verify before parsing/rendering.</summary>
    public string ContentHash { get; set; } = "";
    public string OriginDeviceId { get; set; } = "";
    public string DirectPublicKey { get; set; } = "";
    public string DirectEndpoint { get; set; } = "";
}

/// <summary>The sealed request payload. Kind is "name" or "search".</summary>
public sealed class LookupRequest
{
    public string Kind { get; set; } = "name";
    public string Value { get; set; } = "";
}

internal static class LookupKdf
{
    public const string Info = "kickl/v1/jttp-lookup";
    public const int SaltSize = 16;

    public static byte[] Derive(ReadOnlySpan<byte> rawSecret, ReadOnlySpan<byte> salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA384, rawSecret.ToArray(), 32, salt.ToArray(), Utf8.B(Info));

    public static byte[] Aad(string sessionId, string direction, uint counter) =>
        Utf8.B($"kickl/v1/jttp-lookup|{sessionId}|{direction}|{counter}");
}

/// <summary>The registry side. Holds a snapshot and in-memory session keys only.</summary>
public sealed class PfsRegistryServer : IDisposable
{
    private sealed class Session
    {
        public required byte[] Key;
        public required DateTimeOffset Expires;
        public uint QueryCounter;
        public uint ResponseCounter;
    }

    private readonly List<RegistryRecord> _records;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;

    public PfsRegistryServer(RegistryFile registry, TimeSpan? ttl = null)
    {
        _records = registry.Records.ToList();
        _ttl = ttl ?? TimeSpan.FromHours(1);
    }

    public PfsServerHello Initiate(byte[] clientPublicKeySpki)
    {
        using var server = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var client = ECDiffieHellman.Create();
        client.ImportSubjectPublicKeyInfo(clientPublicKeySpki, out _);

        var raw = server.DeriveRawSecretAgreement(client.PublicKey);
        var salt = RandomNumberGenerator.GetBytes(LookupKdf.SaltSize);
        var key = LookupKdf.Derive(raw, salt);
        CryptographicOperations.ZeroMemory(raw);

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _sessions[id] = new Session { Key = key, Expires = DateTimeOffset.UtcNow.Add(_ttl) };

        return new PfsServerHello
        {
            SessionId = id,
            ServerPublicKeySpki = server.ExportSubjectPublicKeyInfo(),
            Salt = salt
        };
    }

    public PfsSealed HandleQuery(string sessionId, PfsSealed request)
    {
        var session = LiveSession(sessionId);
        session.QueryCounter++;

        byte[] plain;
        try
        {
            plain = Aead.Open(session.Key, request.Nonce, request.Ciphertext,
                LookupKdf.Aad(sessionId, "query", session.QueryCounter));
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("The lookup request failed authentication.");
        }

        LookupRequest req;
        try
        {
            req = JsonSerializer.Deserialize<LookupRequest>(plain)
                  ?? throw new CryptographicException("The lookup request is unreadable.");
        }
        catch (JsonException)
        {
            throw new CryptographicException("The lookup request is unreadable.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        if (string.Equals(req.Kind, "search", StringComparison.OrdinalIgnoreCase))
        {
            var found = Phonebook.Search(new RegistryFile { Records = _records }, req.Value);
            return SealResponse(session, sessionId, found);
        }

        var host = req.Value.Trim().ToLowerInvariant();
        var record = _records.FirstOrDefault(r =>
            string.Equals(r.Name, host, StringComparison.OrdinalIgnoreCase));

        var response = record is null
            ? new LookupResponse { Found = false, Host = host }
            : new LookupResponse
            {
                Found = true,
                Host = record.Name,
                Status = record.Status,
                DocFile = record.DocFile,
                OwnerId = record.OwnerId,
                BadgeId = record.BadgeId,
                Version = record.Version,
                Title = record.Title,
                Headline = record.Headline,
                StoryAuthor = record.StoryAuthor,
                StoryDate = record.StoryDate,
                StoryLocation = record.StoryLocation,
                Tags = record.Tags,
                UpdatedUtc = record.UpdatedUtc,
                ContentHash = record.ContentHash,
                OriginDeviceId = record.OriginDeviceId,
                DirectPublicKey = record.DirectPublicKey,
                DirectEndpoint = record.DirectEndpoint
            };

        return SealResponse(session, sessionId, response);
    }

    private static PfsSealed SealResponse(Session session, string sessionId, object value)
    {
        session.ResponseCounter++;
        var json = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            var (nonce, ct) = Aead.Seal(session.Key, json,
                LookupKdf.Aad(sessionId, "response", session.ResponseCounter));
            return new PfsSealed { Nonce = nonce, Ciphertext = ct };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    public bool Invalidate(string sessionId)
    {
        if (!_sessions.Remove(sessionId, out var session)) return false;
        CryptographicOperations.ZeroMemory(session.Key);
        return true;
    }

    private Session LiveSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new InvalidOperationException("Unknown or already-destroyed lookup session.");
        if (session.Expires < DateTimeOffset.UtcNow)
        {
            Invalidate(sessionId);
            throw new InvalidOperationException("The lookup session expired.");
        }
        return session;
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            CryptographicOperations.ZeroMemory(session.Key);
        _sessions.Clear();
    }
}

/// <summary>The browser side. One ephemeral key pair per lookup session; dispose destroys it.</summary>
public sealed class PfsLookupClient : IDisposable
{
    private readonly ECDiffieHellman _ephemeral;
    private byte[]? _key;
    private string? _sessionId;
    private uint _queryCounter;
    private uint _responseCounter;

    public PfsLookupClient()
    {
        _ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    }

    public byte[] ClientPublicKeySpki => _ephemeral.ExportSubjectPublicKeyInfo();
    public string? SessionId => _sessionId;
    public bool IsEstablished => _key is not null;

    public void Establish(PfsServerHello hello)
    {
        using var server = ECDiffieHellman.Create();
        server.ImportSubjectPublicKeyInfo(hello.ServerPublicKeySpki, out _);

        var raw = _ephemeral.DeriveRawSecretAgreement(server.PublicKey);
        try
        {
            _key = LookupKdf.Derive(raw, hello.Salt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
        _sessionId = hello.SessionId;
    }

    public PfsSealed Query(string host) => Send("name", host);

    /// <summary>Search the live index (title, headline, tags, updated) over the same PFS channel.</summary>
    public PfsSealed Search(string query) => Send("search", query);

    private PfsSealed Send(string kind, string value)
    {
        var key = LiveKey();
        _queryCounter++;
        var json = JsonSerializer.SerializeToUtf8Bytes(new LookupRequest { Kind = kind, Value = value });
        try
        {
            var (nonce, ct) = Aead.Seal(key, json,
                LookupKdf.Aad(_sessionId!, "query", _queryCounter));
            return new PfsSealed { Nonce = nonce, Ciphertext = ct };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    public LookupResponse ReadResponse(PfsSealed sealedResponse) => Read<LookupResponse>(sealedResponse);

    public SearchResult ReadSearch(PfsSealed sealedResponse) => Read<SearchResult>(sealedResponse);

    private T Read<T>(PfsSealed sealedResponse)
    {
        var key = LiveKey();
        _responseCounter++;
        var plain = Aead.Open(key, sealedResponse.Nonce, sealedResponse.Ciphertext,
            LookupKdf.Aad(_sessionId!, "response", _responseCounter));
        try
        {
            return JsonSerializer.Deserialize<T>(plain)
                   ?? throw new CryptographicException("The lookup response is unreadable.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private byte[] LiveKey() =>
        _key ?? throw new InvalidOperationException("The lookup session is not established.");

    /// <summary>Test/diagnostic only: does not reveal the key.</summary>
    public string KeyFingerprint() => Convert.ToHexString(SHA256.HashData(LiveKey())).ToLowerInvariant()[..16];

    public void Dispose()
    {
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
        _sessionId = null;
        _ephemeral.Dispose();
    }
}

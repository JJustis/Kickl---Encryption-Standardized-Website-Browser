using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kickl.Core.Crypto;

namespace Kickl.Core.FreeChat;

public sealed class FreeChatException(string message) : Exception(message);

public sealed record FreeChatUser(int Id, string Username);

public sealed record FreeChatMember(int UserId, string Username, string PublicKeyBase64, bool HasSecret);

public sealed record FreeChatMessage(
    int Id,
    int UserId,
    string Username,
    int Epoch,
    int MsgIndex,
    byte[] Png,
    string CreatedAt,
    bool? ServerAuthentic);

/// <summary>
/// Native client for the existing Pinwheel FreeChat service. It speaks the same PHP/ML-KEM
/// protocol as secupgrade.com/pinwheel-freechat: ML-KEM-1024 wraps a group secret, HKDF-SHA256
/// derives per-epoch and per-message keys, and AES-256-GCM protects the message bytes. The
/// server relays ciphertext it cannot read; message payloads are PNG images by design.
/// </summary>
public sealed class FreeChatClient : IDisposable
{
    public const string DefaultApiBase = "https://secupgrade.com/pinwheel-freechat/chat/api/";
    public const int EpochSeconds = 20;
    private const int MlKemSharedSecretSize = 32;

    private readonly HttpClient _http;
    private readonly Uri _api;
    private string? _csrf;

    private MLKem? _kem;
    private byte[]? _rootSecret;
    private byte[]? _rollSeed;
    private int _rollN;
    private int _localEpoch = -1;
    private int _localMsgIndex;
    private int _lastMsgId;
    private List<FreeChatMember> _members = [];
    private bool _disposed;

    public FreeChatClient(string? apiBase = null)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("KicklBrowser/0.1 FreeChat");

        var baseUri = new Uri(apiBase ?? DefaultApiBase, UriKind.Absolute);
        _api = baseUri.AbsoluteUri.EndsWith('/')
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + "/");
    }

    public FreeChatUser? User { get; private set; }
    public int SessionId { get; private set; }
    public long SessionStartUnix { get; private set; }
    public bool IsCreator { get; private set; }
    public bool IsConnected => _rootSecret is not null;
    public IReadOnlyList<FreeChatMember> Members => _members;

    public async Task<FreeChatUser> SignInAsync(string username, string pin,
        CancellationToken cancellationToken = default) =>
        await AuthenticateAsync("login", username, pin, cancellationToken).ConfigureAwait(false);

    public async Task<FreeChatUser> RegisterAsync(string username, string pin,
        CancellationToken cancellationToken = default) =>
        await AuthenticateAsync("register", username, pin, cancellationToken).ConfigureAwait(false);

    public async Task<FreeChatUser> GuestAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCsrfAsync(cancellationToken).ConfigureAwait(false);
        var response = await PostAsync("guest", [], cancellationToken).ConfigureAwait(false);
        User = ReadUser(response);
        return User;
    }

    /// <summary>Create or join the live room, recover the group secret, and distribute it.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (User is null) throw new FreeChatException("Sign in before connecting to the room.");

        DisposeKem();
        _kem = MLKem.GenerateKey(MLKemAlgorithm.MLKem1024);
        await JoinRoomAsync(cancellationToken).ConfigureAwait(false);

        var otherMembers = _members.Where(m => m.UserId != User.Id).ToArray();
        if (IsCreator && otherMembers.Length == 0)
        {
            _rootSecret = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            var wrapped = await ReadWrappedSecretAsync(cancellationToken).ConfigureAwait(false);
            if (wrapped is null && RoomLostItsKey())
            {
                // The group key lived only in the memory of a process that is gone, and the server
                // refuses a hand-over from anyone who does not hold one, so waiting cannot succeed.
                // The stored ciphertext is already unreadable; open a fresh room instead of hanging.
                await RetireSessionAsync(cancellationToken).ConfigureAwait(false);
                await JoinRoomAsync(cancellationToken).ConfigureAwait(false);
                if (!IsCreator || _members.Any(m => m.UserId != User.Id))
                    throw new FreeChatException(
                        "The room lost its group key and a fresh session could not be opened.");
                _rootSecret = RandomNumberGenerator.GetBytes(32);
            }
            else
            {
                wrapped ??= await WaitForWrappedSecretAsync(TimeSpan.FromSeconds(30),
                    cancellationToken).ConfigureAwait(false);
                var shared = _kem.Decapsulate(Convert.FromBase64String(wrapped.Kem));
                if (shared.Length != MlKemSharedSecretSize)
                    throw new FreeChatException("The ML-KEM shared secret has the wrong size.");
                var wrapKey = Hkdf256(shared, "mlkem-wrap");
                try
                {
                    _rootSecret = Aead.Open(wrapKey,
                        Convert.FromBase64String(wrapped.Iv),
                        Convert.FromBase64String(wrapped.Secret), []);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(wrapKey);
                }
            }
        }

        if (_rootSecret is not { Length: 32 })
            throw new FreeChatException("The room secret is malformed.");

        await DistributePendingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Nobody in the room holds the group key, so no member can hand it over.</summary>
    private bool RoomLostItsKey() =>
        _members.Count > 0 && _members.All(member => !member.HasSecret);

    private async Task JoinRoomAsync(CancellationToken cancellationToken)
    {
        var created = await PostAsync("secure_chat_create_session", [],
            cancellationToken).ConfigureAwait(false);
        SessionId = ReadInt(created, "session_id")
                    ?? throw new FreeChatException("The chat server did not return a session.");

        var joined = await PostAsync("secure_chat_join",
        [
            ("session_id", SessionId.ToString()),
            ("public_key_mlkem_b64", Convert.ToBase64String(_kem!.ExportEncapsulationKey()))
        ], cancellationToken).ConfigureAwait(false);

        SessionStartUnix = ReadLong(joined, "session_start_unix")
                           ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        IsCreator = ReadBool(joined, "is_creator");
        _rollSeed = Convert.FromBase64String(RequireString(joined, "roll_seed_b64"));
        _rollN = ReadInt(joined, "roll_n") ?? 0;
        _lastMsgId = 0;
        _localEpoch = -1;
        _localMsgIndex = 0;

        if (joined.TryGetProperty("members", out var memberList))
            _members = ParseMembers(memberList);
    }

    public async Task<FreeChatUser?> RefreshSessionAsync(CancellationToken cancellationToken = default)
    {
        var me = await GetJsonAsync("me", cancellationToken).ConfigureAwait(false);
        if (me.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
            User = ReadUser(user);
        return User;
    }

    public async Task DistributePendingAsync(CancellationToken cancellationToken = default)
    {
        if (_rootSecret is null || User is null || SessionId == 0) return;

        var response = await GetJsonAsync(
            $"secure_chat_get_members?session_id={SessionId}", cancellationToken)
            .ConfigureAwait(false);
        if (response.TryGetProperty("members", out var membersElement))
            _members = ParseMembers(membersElement);

        var recipients = _members
            .Where(m => !m.HasSecret && m.UserId != User.Id
                        && !string.IsNullOrWhiteSpace(m.PublicKeyBase64))
            .ToArray();

        // Each wrap is independent, and one round-trip per member in sequence is slow enough to
        // look hung in a room with a handful of people, so hand the key out concurrently.
        await Task.WhenAll(recipients.Select(member =>
                DistributeToAsync(member, cancellationToken)))
            .ConfigureAwait(false);
    }

    private async Task DistributeToAsync(FreeChatMember member,
        CancellationToken cancellationToken)
    {
        try
        {
            using var recipient = MLKem.ImportEncapsulationKey(MLKemAlgorithm.MLKem1024,
                Convert.FromBase64String(member.PublicKeyBase64));
            recipient.Encapsulate(out var kemCiphertext, out var sharedSecret);
            var wrapKey = Hkdf256(sharedSecret, "mlkem-wrap");
            try
            {
                var sealedSecret = Aead.Seal(wrapKey, _rootSecret!, []);
                var nonce = sealedSecret.Nonce;
                await PostAsync("secure_chat_distribute_secret",
                [
                    ("session_id", SessionId.ToString()),
                    ("to_user_id", member.UserId.ToString()),
                    ("kem_ciphertext_b64", Convert.ToBase64String(kemCiphertext)),
                    ("wrapped_secret_b64", Convert.ToBase64String(sealedSecret.Ciphertext)),
                    ("wrapped_secret_iv_b64", Convert.ToBase64String(nonce))
                ], cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrapKey);
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException
                                       or FreeChatException or HttpRequestException)
        {
            // A member can leave or change keys between the poll and the wrap; retry next tick.
        }
    }

    /// <summary>Encrypt one PNG message. Returns the server-assigned index.</summary>
    public async Task<int> SendAsync(byte[] png, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_rootSecret is null) throw new FreeChatException("The encrypted room is not ready yet.");

        var epoch = CurrentEpoch();
        if (epoch != _localEpoch)
        {
            _localEpoch = epoch;
            _localMsgIndex = 0;
        }

        var epochKey = Hkdf256(_rootSecret, "epoch-" + epoch);
        var messageKey = Hkdf256(epochKey, "msg-" + _localMsgIndex);
        try
        {
            var sealedMessage = Aead.Seal(messageKey, png, []);
            var ciphertext = sealedMessage.Ciphertext[..^Aead.TagSize];
            var tag = sealedMessage.Ciphertext[^Aead.TagSize..];

            var response = await PostAsync("secure_chat_send",
            [
                ("session_id", SessionId.ToString()),
                ("epoch", epoch.ToString()),
                ("ciphertext_b64", Convert.ToBase64String(ciphertext)),
                ("iv_b64", Convert.ToBase64String(sealedMessage.Nonce)),
                ("gcm_tag_b64", Convert.ToBase64String(tag))
            ], cancellationToken).ConfigureAwait(false);

            var assignedIndex = ReadInt(response, "msg_index") ?? _localMsgIndex;
            _localMsgIndex = assignedIndex + 1;
            return assignedIndex;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(epochKey);
            CryptographicOperations.ZeroMemory(messageKey);
        }
    }

    /// <summary>Fetch and decrypt messages after the last seen id.</summary>
    public async Task<IReadOnlyList<FreeChatMessage>> PollAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_rootSecret is null || SessionId == 0) return [];

        var response = await GetJsonAsync(
            $"secure_chat_poll?session_id={SessionId}&after_id={_lastMsgId}", cancellationToken)
            .ConfigureAwait(false);
        if (!response.TryGetProperty("messages", out var messages)
            || messages.ValueKind != JsonValueKind.Array)
            return [];

        bool? serverAuthentic = null;
        if (messages.GetArrayLength() > 0)
        {
            var first = messages[0];
            var rollTag = ReadString(first, "roll_tag");
            if (rollTag is { Length: 64 })
                serverAuthentic = VerifyRollTag(messages, ReadInt(first, "roll_n") ?? 0, rollTag);
        }

        var decrypted = new List<FreeChatMessage>();
        foreach (var message in messages.EnumerateArray())
        {
            var id = ReadInt(message, "id") ?? 0;
            _lastMsgId = Math.Max(_lastMsgId, id);

            try
            {
                var epoch = ReadInt(message, "epoch") ?? 0;
                var index = ReadInt(message, "msg_index") ?? 0;
                var epochKey = Hkdf256(_rootSecret, "epoch-" + epoch);
                var messageKey = Hkdf256(epochKey, "msg-" + index);
                byte[] plaintext;
                try
                {
                    var ciphertext = Convert.FromBase64String(RequireString(message, "ciphertext"));
                    var tag = Convert.FromBase64String(RequireString(message, "gcm_tag"));
                    var combined = new byte[ciphertext.Length + tag.Length];
                    ciphertext.CopyTo(combined, 0);
                    tag.CopyTo(combined, ciphertext.Length);
                    plaintext = Aead.Open(messageKey,
                        Convert.FromBase64String(RequireString(message, "iv")), combined, []);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(epochKey);
                    CryptographicOperations.ZeroMemory(messageKey);
                }

                decrypted.Add(new FreeChatMessage(
                    id,
                    ReadInt(message, "sender_user_id") ?? 0,
                    ReadString(message, "username") ?? "member",
                    epoch,
                    index,
                    plaintext,
                    ReadString(message, "created_at") ?? "",
                    serverAuthentic));
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException
                                           or JsonException or ArgumentException)
            {
                // A message from before this member received the group secret cannot be opened.
                // Keep its id consumed so the next poll does not loop on it.
            }
        }

        return decrypted;
    }

    public async Task RetireSessionAsync(CancellationToken cancellationToken = default)
    {
        if (SessionId == 0) return;
        await PostAsync("secure_chat_retire_session",
            [("session_id", SessionId.ToString())], cancellationToken).ConfigureAwait(false);
        SessionId = 0;
        _rootSecret = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeKem();
        if (_rootSecret is not null) CryptographicOperations.ZeroMemory(_rootSecret);
        if (_rollSeed is not null) CryptographicOperations.ZeroMemory(_rollSeed);
        _rootSecret = null;
        _rollSeed = null;
        _http.Dispose();
    }

    private async Task<FreeChatUser> AuthenticateAsync(string action, string username, string pin,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (username.Trim().Length < 3 || pin.Length < 4)
            throw new FreeChatException("Username must be at least 3 characters and PIN at least 4.");

        await EnsureCsrfAsync(cancellationToken).ConfigureAwait(false);
        var response = await PostAsync(action,
        [
            ("username", username.Trim()),
            ("pin", pin)
        ], cancellationToken).ConfigureAwait(false);
        User = ReadUser(response);
        return User;
    }

    private async Task<WrappedSecret?> ReadWrappedSecretAsync(CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync(
            $"secure_chat_get_wrapped_secret?session_id={SessionId}", cancellationToken)
            .ConfigureAwait(false);
        var kem = ReadString(response, "kem_ciphertext_b64");
        var secret = ReadString(response, "wrapped_secret_b64");
        var iv = ReadString(response, "wrapped_secret_iv_b64");
        return kem is not null && secret is not null && iv is not null
            ? new WrappedSecret(kem, secret, iv)
            : null;
    }

    private async Task<WrappedSecret> WaitForWrappedSecretAsync(TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await ReadWrappedSecretAsync(cancellationToken).ConfigureAwait(false) is { } wrapped)
                return wrapped;

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        throw new FreeChatException(
            "Timed out waiting for the group key. Nobody online can hand it over yet; " +
            "ask a member to open FreeChat, or retire the session and start fresh.");
    }

    private bool VerifyRollTag(JsonElement messages, int n, string expectedHex)
    {
        if (_rollSeed is null) return false;
        if (n != _rollN) _rollN = n; // resync after a missed poll instead of freezing forever

        var key = Hkdf256(_rollSeed, "roll-" + n);
        try
        {
            var payload = string.Join('|', messages.EnumerateArray()
                .Select(m => $"{ReadInt(m, "id")}:{ReadString(m, "ciphertext")}"));
            var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));
            var actual = Convert.ToHexString(mac).ToLowerInvariant();
            _rollN = n + 1;
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(expectedHex.ToLowerInvariant()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private int CurrentEpoch() =>
        (int) ((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - SessionStartUnix) / EpochSeconds);

    private async Task EnsureCsrfAsync(CancellationToken cancellationToken)
    {
        var me = await GetJsonAsync("me", cancellationToken).ConfigureAwait(false);
        _csrf = ReadString(me, "csrf_token");
        if (me.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
            User = ReadUser(user);
    }

    private async Task<JsonElement> PostAsync(string action,
        IReadOnlyList<(string Key, string Value)> fields, CancellationToken cancellationToken,
        bool retried = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_api, action + ".php"));
        request.Headers.TryAddWithoutValidation("X-CSRF-Token", _csrf ?? "");
        request.Content = new FormUrlEncodedContent(fields.Select(f =>
            new KeyValuePair<string, string>(f.Key, f.Value)));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Forbidden && !retried)
        {
            await EnsureCsrfAsync(cancellationToken).ConfigureAwait(false);
            return await PostAsync(action, fields, cancellationToken, retried: true)
                .ConfigureAwait(false);
        }

        return ParseResponse(response, body, action);
    }

    private async Task<JsonElement> GetJsonAsync(string actionQuery,
        CancellationToken cancellationToken)
    {
        var queryAt = actionQuery.IndexOf('?');
        var path = queryAt < 0
            ? actionQuery + ".php"
            : actionQuery[..queryAt] + ".php" + actionQuery[queryAt..];
        using var response = await _http.GetAsync(new Uri(_api, path),
            cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseResponse(response, body, actionQuery);
    }

    private static JsonElement ParseResponse(HttpResponseMessage response, string body,
        string action)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new FreeChatException(
                $"The chat server returned an unreadable response for {action} ({(int) response.StatusCode}).");
        }

        using (document)
        {
            var root = document.RootElement.Clone();
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error))
                throw new FreeChatException(error.GetString() ?? "The chat server refused the request.");
            if (!response.IsSuccessStatusCode)
                throw new FreeChatException(
                    $"The chat server returned {(int) response.StatusCode} for {action}.");
            return root;
        }
    }

    private static FreeChatUser ReadUser(JsonElement element) => new(
        ReadInt(element, "id") ?? 0,
        ReadString(element, "username") ?? "member");

    private static List<FreeChatMember> ParseMembers(JsonElement element)
    {
        var members = new List<FreeChatMember>();
        if (element.ValueKind != JsonValueKind.Array) return members;
        foreach (var member in element.EnumerateArray())
        {
            var id = ReadInt(member, "user_id") ?? 0;
            members.Add(new FreeChatMember(
                id,
                ReadString(member, "username") ?? "user-" + id,
                ReadString(member, "public_key_mlkem_b64") ?? "",
                ReadBool(member, "has_secret")));
        }
        return members;
    }

    private static byte[] Hkdf256(ReadOnlySpan<byte> ikm, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm.ToArray(), 32, [],
            Encoding.UTF8.GetBytes(info));

    private static string RequireString(JsonElement element, string name) =>
        ReadString(element, name) ?? throw new FreeChatException($"Missing {name} in chat response.");

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => value.GetString() is "1" or "true",
            _ => false
        };
    }

    private void DisposeKem()
    {
        _kem?.Dispose();
        _kem = null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record WrappedSecret(string Kem, string Secret, string Iv);
}

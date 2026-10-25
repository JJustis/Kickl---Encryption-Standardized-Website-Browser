using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kickl.Core.Crypto;
using Kickl.Core.Util;
using Microsoft.Data.Sqlite;

namespace Kickl.Core.Mail;

public sealed class MailRecordSummary
{
    public string Id { get; set; } = "";
    public string Direction { get; set; } = "in"; // in | out
    public string PeerShortId { get; set; } = "";
    public string PeerHandle { get; set; } = "";
    public long CreatedAt { get; set; }
    public bool Unread { get; set; }
    public int ImageBytes { get; set; }
}

public sealed class MailRecord
{
    public int V { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Direction { get; set; } = "in";
    public string PeerShortId { get; set; } = "";
    public string PeerHandle { get; set; } = "";
    public long CreatedAt { get; set; }
    public bool Unread { get; set; }
    public byte[] Image { get; set; } = [];
    public byte[] KeyNonce { get; set; } = [];
    public byte[] KeyCiphertext { get; set; } = [];
    public MailEnvelope Envelope { get; set; } = new();
}

/// <summary>
/// Per-profile SQLite mail store. The mail password unwraps a random 32-byte store key;
/// the identity and every image message are sealed with AES-256-GCM before they are
/// written to SQLite. A wrong password fails the GCM check and changes nothing.
/// No message ever goes to the Pinwheel server: it is only used to look up the
/// recipient's public bundle and direct endpoint.
/// </summary>
public sealed class MailStore : IDisposable
{
    private const int Iterations = 600_000;
    private const int MskSize = 32;
    private static readonly byte[] VaultAad = Encoding.UTF8.GetBytes("kickl/v1/mail-vault|1");
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly DataPaths _paths;
    private readonly object _gate = new();
    private byte[]? _msk;
    private List<MailRecordSummary> _index = [];
    private MailIdentity? _identity;

    public MailStore(DataPaths paths)
    {
        _paths = paths;
        Directory.CreateDirectory(paths.MailRoot);
        EnsureSchema();
    }

    public string DatabasePath => _paths.MailDatabase;

    public bool HasVault
    {
        get
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM mail_vault";
            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }
    }

    public bool IsUnlocked => _msk is not null;
    public MailIdentity? Identity => _identity;
    public MailIdentityBundle? Bundle => _identity?.Bundle;
    public string? Token => _identity?.Token;

    public bool IsRegistered
    {
        get
        {
            if (_msk is null) return false;
            try { return LoadIdentityFile()?.Registered == true; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public void CreateVault(string password)
    {
        ValidatePassword(password);
        if (HasVault) throw new InvalidOperationException("A mail password already exists for this profile.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var msk = RandomNumberGenerator.GetBytes(MskSize);
        var kek = Derive(password, salt, Iterations);
        try
        {
            var (nonce, wrapped) = Aead.Seal(kek, msk, VaultAad);
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO mail_vault (id, version, salt, iterations, nonce, wrapped_msk, created_at) " +
                "VALUES (1, 1, $salt, $iterations, $nonce, $wrapped, $created)";
            command.Parameters.AddWithValue("$salt", salt);
            command.Parameters.AddWithValue("$iterations", Iterations);
            command.Parameters.AddWithValue("$nonce", nonce);
            command.Parameters.AddWithValue("$wrapped", wrapped);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
            _msk = msk;
            _index = [];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>Unlock the local SQLite vault. Throws <see cref="CryptographicException"/> on a wrong password.</summary>
    public void Unlock(string password)
    {
        if (_msk is not null) return;
        var (salt, iterations, nonce, wrapped) = ReadVault();
        var kek = Derive(password, salt, iterations);
        byte[] msk;
        try
        {
            msk = Aead.Open(kek, nonce, wrapped, VaultAad);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("Wrong mail password.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }

        _msk = msk;
        _index = LoadSummaries();
        _identity = LoadIdentity();
    }

    public void ChangePassword(string oldPassword, string newPassword)
    {
        ValidatePassword(newPassword);
        if (_msk is null) Unlock(oldPassword);
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = Derive(newPassword, salt, Iterations);
        try
        {
            var (nonce, wrapped) = Aead.Seal(kek, _msk!, VaultAad);
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE mail_vault SET salt=$salt, iterations=$iterations, nonce=$nonce, " +
                "wrapped_msk=$wrapped WHERE id=1";
            command.Parameters.AddWithValue("$salt", salt);
            command.Parameters.AddWithValue("$iterations", Iterations);
            command.Parameters.AddWithValue("$nonce", nonce);
            command.Parameters.AddWithValue("$wrapped", wrapped);
            command.ExecuteNonQuery();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>Load the identity if present; otherwise generate and persist a new one.</summary>
    public MailIdentity EnsureIdentity(string handle)
    {
        if (_identity is not null) return _identity;
        if (_msk is null) throw new InvalidOperationException("Unlock the mail vault first.");

        var identity = LoadIdentity();
        if (identity is not null)
        {
            _identity = identity;
            return identity;
        }

        var generated = MailIdentity.Generate(handle);
        _identity = generated;
        SaveIdentity(generated, registered: false);
        return generated;
    }

    public void MarkRegistered()
    {
        if (_identity is null) throw new InvalidOperationException("No mail identity is loaded.");
        SaveIdentity(_identity, registered: true);
    }

    /// <summary>Re-sign the bundle after an endpoint change and persist it locally.</summary>
    public void UpdateEndpoint(string endpoint)
    {
        if (_identity is null) throw new InvalidOperationException("No mail identity is loaded.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The mail endpoint must be an absolute http(s) URL.", nameof(endpoint));
        _identity.Bundle.Endpoint = endpoint.TrimEnd('/');
        _identity.Bundle.Sig = _identity.Sign(_identity.Bundle.CanonicalBytes());
        SaveIdentity(_identity, registered: false);
    }

    public IReadOnlyList<MailRecordSummary> List()
    {
        lock (_gate)
        {
            return _index
                .OrderByDescending(m => m.CreatedAt)
                .ThenByDescending(m => m.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public bool HasMessage(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM mail_messages WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    public MailRecord? Load(string id)
    {
        if (_msk is null) throw new InvalidOperationException("Unlock the mail vault first.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sealed FROM mail_messages WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        var value = command.ExecuteScalar();
        if (value is not byte[] sealedRecord) return null;
        var plain = ProfileDataCipher.Open(_msk, "mail-message", sealedRecord);
        try
        {
            return JsonSerializer.Deserialize<MailRecord>(plain, Json)
                   ?? throw new InvalidOperationException("The local mail record is unreadable.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void SaveOutgoing(SealedMail mail, string peerHandle)
    {
        if (_identity is null) throw new InvalidOperationException("No mail identity is loaded.");
        SaveRecord(BuildRecord(mail.Envelope, mail.Image, mail.MessageKey, "out", peerHandle));
    }

    public void SaveIncoming(MailEnvelope envelope, byte[] image, byte[] messageKey)
    {
        var record = BuildRecord(envelope, image, messageKey, "in", envelope.FromHandle);
        record.Unread = true;
        SaveRecord(record);
    }

    public MailContent Read(string id)
    {
        if (_msk is null) throw new InvalidOperationException("Unlock the mail vault first.");
        var record = Load(id) ?? throw new KeyNotFoundException("That mail message is not in this profile.");
        var key = MailCrypto.OpenLocalKey(_msk, record.Id, record.KeyNonce, record.KeyCiphertext);
        try
        {
            return MailCrypto.OpenWithKey(key, record.Image, record.Envelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void MarkRead(string id)
    {
        var record = Load(id);
        if (record is null || !record.Unread) return;
        record.Unread = false;
        SaveRecord(record);
    }

    public void Lock()
    {
        _identity?.Dispose();
        _identity = null;
        if (_msk is not null) CryptographicOperations.ZeroMemory(_msk);
        _msk = null;
        lock (_gate) _index = [];
    }

    public void Dispose() => Lock();

    private MailRecord BuildRecord(MailEnvelope envelope, byte[] image, byte[] messageKey,
        string direction, string peerHandle)
    {
        if (_msk is null) throw new InvalidOperationException("Unlock the mail vault first.");
        var (nonce, sealedKey) = MailCrypto.SealLocalKey(_msk, envelope.Id, messageKey);
        // The RSA-wrapped key is a transport artefact. Once the message is in this
        // device's vault, only the mail-password-sealed message key is retained, so a
        // stolen SQLite file plus a later RSA key leak does not reopen old mail.
        var localEnvelope = new MailEnvelope
        {
            V = envelope.V,
            Id = envelope.Id,
            ToShortId = envelope.ToShortId,
            FromId = envelope.FromId,
            FromShortId = envelope.FromShortId,
            FromHandle = envelope.FromHandle,
            CreatedAt = envelope.CreatedAt,
            Nonce = envelope.Nonce,
            ImageHash = envelope.ImageHash,
            SenderBundle = envelope.SenderBundle
        };
        return new MailRecord
        {
            Id = envelope.Id,
            Direction = direction,
            PeerShortId = direction == "out" ? envelope.ToShortId : envelope.FromShortId,
            PeerHandle = peerHandle,
            CreatedAt = envelope.CreatedAt,
            Unread = direction == "in",
            Image = image,
            KeyNonce = nonce,
            KeyCiphertext = sealedKey,
            Envelope = localEnvelope
        };
    }

    private void SaveRecord(MailRecord record)
    {
        if (_msk is null) throw new InvalidOperationException("Unlock the mail vault first.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        try
        {
            var sealedRecord = ProfileDataCipher.Protect(_msk, "mail-message", plain);
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO mail_messages (id, sealed) VALUES ($id, $sealed) " +
                "ON CONFLICT(id) DO UPDATE SET sealed = excluded.sealed";
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$sealed", sealedRecord);
            command.ExecuteNonQuery();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        lock (_gate)
        {
            _index.RemoveAll(m => m.Id == record.Id);
            _index.Add(new MailRecordSummary
            {
                Id = record.Id,
                Direction = record.Direction,
                PeerShortId = record.PeerShortId,
                PeerHandle = record.PeerHandle,
                CreatedAt = record.CreatedAt,
                Unread = record.Unread,
                ImageBytes = record.Image.Length
            });
        }
    }

    private List<MailRecordSummary> LoadSummaries()
    {
        var summaries = new List<MailRecordSummary>();
        if (_msk is null) return summaries;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sealed FROM mail_messages";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetValue(0) is not byte[] sealedRecord) continue;
            try
            {
                var plain = ProfileDataCipher.Open(_msk, "mail-message", sealedRecord);
                try
                {
                    var record = JsonSerializer.Deserialize<MailRecord>(plain, Json);
                    if (record is null) continue;
                    summaries.Add(new MailRecordSummary
                    {
                        Id = record.Id,
                        Direction = record.Direction,
                        PeerShortId = record.PeerShortId,
                        PeerHandle = record.PeerHandle,
                        CreatedAt = record.CreatedAt,
                        Unread = record.Unread,
                        ImageBytes = record.Image.Length
                    });
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                // Skip a damaged row; never let one bad record hide the whole inbox.
            }
        }
        return summaries;
    }

    private void SaveIdentity(MailIdentity identity, bool registered)
    {
        if (_msk is null) throw new InvalidOperationException("Unlock the mail vault first.");
        var file = new MailIdentityFile
        {
            Bundle = identity.Bundle.ToJson(),
            Token = identity.Token,
            RsaPkcs8 = identity.RsaPkcs8,
            MlDsaPkcs8 = identity.MlDsaPkcs8,
            Registered = registered
        };
        var plain = JsonSerializer.SerializeToUtf8Bytes(file, Json);
        try
        {
            var sealedIdentity = ProfileDataCipher.Protect(_msk, "mail-identity", plain);
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO mail_identity (id, sealed) VALUES (1, $sealed) " +
                "ON CONFLICT(id) DO UPDATE SET sealed = excluded.sealed";
            command.Parameters.AddWithValue("$sealed", sealedIdentity);
            command.ExecuteNonQuery();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private MailIdentityFile? LoadIdentityFile()
    {
        if (_msk is null) return null;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sealed FROM mail_identity WHERE id = 1";
        var value = command.ExecuteScalar();
        if (value is not byte[] sealedIdentity) return null;
        try
        {
            var plain = ProfileDataCipher.Open(_msk, "mail-identity", sealedIdentity);
            try
            {
                return JsonSerializer.Deserialize<MailIdentityFile>(plain, Json);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            throw new InvalidOperationException("The local mail identity could not be opened.", ex);
        }
    }

    private MailIdentity? LoadIdentity()
    {
        var file = LoadIdentityFile();
        return file is null ? null : RestoreIdentity(file);
    }

    private static MailIdentity RestoreIdentity(MailIdentityFile file)
    {
        var bundle = MailIdentityBundle.Parse(file.Bundle);
        return MailIdentity.Restore(bundle, file.Token, file.RsaPkcs8, file.MlDsaPkcs8);
    }

    private (byte[] Salt, int Iterations, byte[] Nonce, byte[] Wrapped) ReadVault()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT version, salt, iterations, nonce, wrapped_msk FROM mail_vault WHERE id = 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("No mail password has been set for this profile.");
        if (reader.GetInt32(0) != 1 || reader.GetInt32(2) < 100_000)
            throw new InvalidOperationException("The mail vault uses an unsupported format.");
        return ((byte[]) reader.GetValue(1), reader.GetInt32(2), (byte[]) reader.GetValue(3),
            (byte[]) reader.GetValue(4));
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(_paths.MailRoot);
        var connection = new SqliteConnection($"Data Source={_paths.MailDatabase};Pooling=False");
        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS mail_vault (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                version INTEGER NOT NULL,
                salt BLOB NOT NULL,
                iterations INTEGER NOT NULL,
                nonce BLOB NOT NULL,
                wrapped_msk BLOB NOT NULL,
                created_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS mail_identity (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                sealed BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS mail_messages (
                id TEXT PRIMARY KEY,
                sealed BLOB NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    private static void ValidatePassword(string password)
    {
        if (password.Length < 8)
            throw new ArgumentException("The mail password must be at least 8 characters.", nameof(password));
        if (password.Length > 512)
            throw new ArgumentException("The mail password is too long.", nameof(password));
    }

    private sealed class MailIdentityFile
    {
        public string Bundle { get; set; } = "";
        public string Token { get; set; } = "";
        public byte[] RsaPkcs8 { get; set; } = [];
        public byte[] MlDsaPkcs8 { get; set; } = [];
        public bool Registered { get; set; }
    }
}

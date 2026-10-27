using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Kickl.Core.Util;

namespace Kickl.Core.Mail;

/// <summary>
/// High-level mail flow: password vault → local identity → Pinwheel credential lookup →
/// direct sealed delivery to the recipient device → local SQLite image store.
/// There is no server mailbox and no server-side message row.
/// </summary>
public sealed class MailService : IDisposable
{
    private readonly MailClient _client;
    private readonly SemaphoreSlim _ops = new(1, 1);
    private MailDeliveryServer? _listener;
    private bool _disposed;

    public MailService(DataPaths paths, MailClient? client = null)
    {
        Store = new MailStore(paths);
        _client = client ?? new MailClient();
    }

    public MailStore Store { get; }
    public DateTimeOffset? LastSyncUtc { get; private set; }
    public int LastDelivered { get; private set; }
    public bool IsUnlocked => Store.IsUnlocked;
    public bool HasPassword => Store.HasVault;
    public string? ShortId => Store.Bundle?.ShortId;
    public string? Handle => Store.Bundle?.Handle;
    public string? Endpoint => Store.Bundle?.Endpoint;
    public int? ListeningPort => _listener?.Port;

    /// <summary>First use: create the independent mail password, identity and direct listener.</summary>
    public async Task SetupAsync(string password, string handle,
        CancellationToken cancellationToken = default)
    {
        Store.CreateVault(password);
        Store.EnsureIdentity(handle);
        StartListener();
        await RegisterAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UnlockAsync(string password, string handle,
        CancellationToken cancellationToken = default)
    {
        Store.Unlock(password);
        Store.EnsureIdentity(handle);
        StartListener();
        if (!Store.IsRegistered)
            await RegisterAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RegisterAsync(CancellationToken cancellationToken = default)
    {
        var identity = Store.Identity
                       ?? throw new InvalidOperationException("Unlock mail before registering an identity.");
        await _client.RegisterAsync(identity.Bundle, identity.Token, cancellationToken)
            .ConfigureAwait(false);
        Store.MarkRegistered();
    }

    /// <summary>Update the advertised direct endpoint, rebind the listener, then republish.</summary>
    public async Task UpdateEndpointAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        await _ops.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Store.UpdateEndpoint(endpoint);
            StartListener();
            await RegisterAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ops.Release();
        }
    }

    public async Task SendAsync(string toShortId, string subject, string body,
        byte[]? coverPng = null, CancellationToken cancellationToken = default)
    {
        await _ops.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identity = Store.Identity
                           ?? throw new InvalidOperationException("Unlock mail before sending.");
            if (!Store.IsRegistered)
                await RegisterAsync(cancellationToken).ConfigureAwait(false);

            var recipient = await _client.LookupAsync(toShortId, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(recipient.ShortId, identity.Bundle.ShortId, StringComparison.Ordinal))
                recipient = identity.Bundle;

            var sealedMail = MailCrypto.Seal(identity, recipient, subject, body, coverPng);
            try
            {
                try
                {
                    await _client.DeliverAsync(recipient, sealedMail.Envelope, sealedMail.Image,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException first)
                {
                    // If the sender and recipient are two profiles on this same machine, the
                    // advertised LAN address can be blocked by the firewall while loopback is
                    // still reachable. Try loopback once, then report the real failure.
                    var loopback = MailEndpoint.Loopback(recipient.Endpoint);
                    if (!string.Equals(loopback, recipient.Endpoint.TrimEnd('/'), StringComparison.Ordinal))
                    {
                        try
                        {
                            var localBundle = CloneWithEndpoint(recipient, loopback);
                            await _client.DeliverAsync(localBundle, sealedMail.Envelope, sealedMail.Image,
                                cancellationToken).ConfigureAwait(false);
                            Store.SaveOutgoing(sealedMail, recipient.Handle);
                            return;
                        }
                        catch (Exception) { /* fall through to the original endpoint error */ }
                    }

                    if (first.GetBaseException() is System.Security.Authentication.AuthenticationException)
                        throw new MailServiceException(
                            "The device at the recipient's endpoint did not present the TLS key they published, so the delivery was refused. " +
                            "Either they changed devices and have not republished, or something is intercepting the connection.", null, first);

                    throw new MailServiceException(
                        "Could not reach the recipient's direct endpoint " + recipient.Endpoint +
                        ". The device must be online with mail unlocked, the port must be reachable " +
                        "through Windows Firewall/NAT, and PUBLISH must advertise that endpoint. " +
                        "Detail: " + first.Message, null, first);
                }

                Store.SaveOutgoing(sealedMail, recipient.Handle);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sealedMail.MessageKey);
            }
        }
        finally
        {
            _ops.Release();
        }
    }

    /// <summary>
    /// Called by the direct listener. Verifies the sender bundle, envelope signature and
    /// image hash, decrypts with the unlocked RSA key, then seals the message into SQLite.
    /// Nothing is written before every check passes.
    /// </summary>
    public async Task<bool> AcceptDeliveryAsync(MailEnvelope envelope, byte[] image,
        CancellationToken cancellationToken = default)
    {
        await _ops.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identity = Store.Identity;
            if (identity is null || !Store.IsUnlocked)
                throw new MailServiceException("Mail is locked on the recipient device.", HttpStatusCode.ServiceUnavailable);
            if (!string.Equals(envelope.ToShortId, identity.Bundle.ShortId, StringComparison.Ordinal))
                throw new MailServiceException("That message is addressed to another PFS id.", HttpStatusCode.BadRequest);
            if (Store.HasMessage(envelope.Id))
                return false;

            OpenedMail opened;
            try
            {
                opened = MailCrypto.Open(envelope, image, identity.Rsa, identity.Bundle.ShortId);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                throw new MailServiceException("The delivered message failed verification: " + ex.Message,
                    HttpStatusCode.BadRequest);
            }

            try
            {
                Store.SaveIncoming(envelope, image, opened.MessageKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(opened.MessageKey);
            }
            LastDelivered++;
            LastSyncUtc = DateTimeOffset.UtcNow;
            return true;
        }
        finally
        {
            _ops.Release();
        }
    }

    public MailContent Read(string id)
    {
        var content = Store.Read(id);
        Store.MarkRead(id);
        return content;
    }

    private static MailIdentityBundle CloneWithEndpoint(MailIdentityBundle bundle, string endpoint) =>
        new()
        {
            V = bundle.V,
            Id = bundle.Id,
            ShortId = bundle.ShortId,
            Handle = bundle.Handle,
            Endpoint = endpoint,
            RsaPublicKey = bundle.RsaPublicKey,
            MlDsaPublicKey = bundle.MlDsaPublicKey,
            CreatedAt = bundle.CreatedAt,
            Sig = bundle.Sig
        };

    public void StartListener()
    {
        var endpoint = Store.Bundle?.Endpoint;
        if (endpoint is null) return;
        _listener?.Stop();
        _listener = null;
        var certificate = Store.Identity?.TlsCertificate
                          ?? throw new InvalidOperationException("The mail identity has no TLS key.");
        var listener = new MailDeliveryServer(this, endpoint, certificate);
        listener.Start();
        _listener = listener;
    }

    public void Lock()
    {
        _listener?.Stop();
        _listener = null;
        Store.Lock();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Lock();
        Store.Dispose();
        _client.Dispose();
        _ops.Dispose();
    }
}

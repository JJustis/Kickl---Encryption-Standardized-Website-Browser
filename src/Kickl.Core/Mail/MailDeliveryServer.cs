using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Kickl.Core.Mail;

/// <summary>
/// The listener that accepts sealed mail straight from another device. It speaks TLS 1.3 only, with this device's own key (which the
/// identity's signed endpoint pins), so the envelope's metadata (who wrote to whom, the sender's handle and the time) is not readable
/// or alterable on the wire. It is exposed on every interface, so it is also built to survive hostile peers: a cap on simultaneous
/// connections, a handshake timeout, a hard deadline per connection, bounded reads, and error text that never echoes internals.
/// It is not a mailbox: a message exists only in the local SQLite vault after <see cref="MailService.AcceptDeliveryAsync"/> verifies it.
/// </summary>
public sealed class MailDeliveryServer : IDisposable
{
    private const int MaxRequestBytes = MailCrypto.MaxImageBytes + 32 * 1024;
    public const int MaxConnections = 16;

    private readonly MailService _service;
    private readonly X509Certificate2 _certificate;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private Task? _loop;
    private bool _disposed;

    /// <summary>The longest one connection may live, handshake included. Tests shorten it.</summary>
    internal TimeSpan ConnectionDeadline { get; set; } = TimeSpan.FromSeconds(30);

    /// <param name="certificate">Owned by the mail identity; this server does not dispose it.</param>
    public MailDeliveryServer(MailService service, string endpoint, X509Certificate2 certificate)
    {
        _service = service;
        _certificate = certificate;
        var port = MailEndpoint.PortOf(endpoint);
        _listener = new TcpListener(IPAddress.Any, port);
        Port = port;
    }

    public int Port { get; }
    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null) return;
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _listener.Stop(); } catch (SocketException) { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _loop = null;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            // Every slot busy: drop the newcomer at once. A peer that opens connections and sits on them cannot starve real delivery
            // beyond the cap, and it costs us nothing but the accept.
            if (!_slots.Wait(0))
            {
                client.Dispose();
                continue;
            }

            _ = HandleClientAsync(client, cancellationToken).ContinueWith(_ => _slots.Release(), TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(serverToken))
        {
            deadline.CancelAfter(ConnectionDeadline);
            var token = deadline.Token;
            SslStream? tls = null;
            try
            {
                tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls13,
                    ClientCertificateRequired = false,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, token).ConfigureAwait(false);

                await ServeAsync(tls, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException
                                           or InvalidDataException or SocketException or ObjectDisposedException)
            {
                // A failed handshake (a plain-http client, the wrong protocol version, a scanner), a slow or silent peer past its
                // deadline, or a malformed request: close without a word. There is nothing a hostile peer should learn from us.
            }
            finally
            {
                if (tls is not null) await tls.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(Stream stream, CancellationToken token)
    {
        try
        {
            var (requestLine, body) = await ReadRequestAsync(stream, token).ConfigureAwait(false);
            if (!string.Equals(requestLine, "POST " + MailEndpoint.Path + " HTTP/1.1", StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, 404, "{\"error\":\"Unknown endpoint.\"}", token).ConfigureAwait(false);
                return;
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("envelope", out var envelopeElement)
                || envelopeElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("image", out var imageElement)
                || imageElement.ValueKind != JsonValueKind.String)
            {
                await WriteResponseAsync(stream, 400, "{\"error\":\"The delivery is incomplete.\"}", token).ConfigureAwait(false);
                return;
            }

            var envelope = MailEnvelope.Parse(envelopeElement.GetRawText());
            var image = Convert.FromBase64String(imageElement.GetString() ?? "");
            var accepted = await _service.AcceptDeliveryAsync(envelope, image, token).ConfigureAwait(false);
            await WriteResponseAsync(stream, accepted ? 200 : 409,
                accepted ? "{\"ok\":true}" : "{\"error\":\"The message is already stored.\"}", token).ConfigureAwait(false);
        }
        catch (MailServiceException ex)
        {
            // These messages are written for the sender ("the recipient is locked", "bad signature"); they hold no internals.
            await WriteResponseAsync(stream, (int) (ex.Status ?? HttpStatusCode.BadRequest),
                JsonSerializer.Serialize(new { error = ex.Message }), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidDataException)
        {
            // Anything else is either a malformed request or our own bug; neither is the sender's business, and the exception text
            // could carry paths or parser internals.
            await WriteResponseAsync(stream, 400, "{\"error\":\"The delivery could not be processed.\"}", token).ConfigureAwait(false);
        }
    }

    private static async Task<(string RequestLine, byte[] Body)> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read <= 0) throw new InvalidDataException("The mail request ended before its headers.");
            buffer.Write(chunk, 0, read);
            if (buffer.Length > 64 * 1024) throw new InvalidDataException("The mail headers are too large.");
            headerEnd = IndexOfHeaderEnd(buffer.GetBuffer(), (int) buffer.Length);
        }

        var all = buffer.ToArray();
        var header = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = header.Split("\r\n", StringSplitOptions.None);
        var firstLine = lines[0];

        long contentLength = -1;
        foreach (var line in lines.Skip(1))
        {
            // We take a length and nothing else: chunked bodies and a repeated Content-Length are how requests get smuggled.
            if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Chunked mail requests are not accepted.");
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
            if (contentLength >= 0) throw new InvalidDataException("The mail request has two lengths.");
            if (!long.TryParse(line[15..].Trim(), out contentLength)) throw new InvalidDataException("The mail request has an invalid size.");
        }
        if (contentLength <= 0 || contentLength > MaxRequestBytes)
            throw new InvalidDataException("The mail request has an invalid size.");

        var bodyStart = headerEnd + 4;
        var body = new byte[contentLength];
        var already = all.Length - bodyStart;
        if (already > 0) Buffer.BlockCopy(all, bodyStart, body, 0, (int) Math.Min(already, contentLength));
        var offset = (int) Math.Min(already, contentLength);
        while (offset < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read <= 0) throw new InvalidDataException("The mail body ended early.");
            offset += read;
        }
        return (firstLine.Trim(), body);
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' &&
                buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                return i;
        return -1;
    }

    private static async Task WriteResponseAsync(Stream stream, int status, string json, CancellationToken token)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            409 => "Conflict",
            503 => "Service Unavailable",
            _ => "Error"
        };
        var body = Encoding.UTF8.GetBytes(json);
        var header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\n" +
                     $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public void Dispose() => Stop();
}

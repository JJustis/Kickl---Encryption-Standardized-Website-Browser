using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kickl.Core.Util;

namespace Kickl.Core.Mail;

/// <summary>
/// A small HTTP/1.1 listener for direct delivery. Pinwheel is only asked for the
/// recipient's endpoint and public bundle; the sealed PNG is posted straight here.
/// The listener is not a mailbox: a message exists only in the local SQLite vault
/// after <see cref="MailService.AcceptDeliveryAsync"/> verifies and seals it.
/// </summary>
public sealed class MailDeliveryServer : IDisposable
{
    private const int MaxRequestBytes = MailCrypto.MaxImageBytes + 32 * 1024;
    private readonly MailService _service;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _disposed;

    public MailDeliveryServer(MailService service, string endpoint)
    {
        _service = service;
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
            catch (Exception ex) when (ex is OperationCanceledException or SocketException
                                           or ObjectDisposedException)
            {
                return;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.ReceiveTimeout = 30_000;
            client.SendTimeout = 30_000;
            try
            {
                await using var stream = client.GetStream();
                var (requestLine, body) = await ReadRequestAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(requestLine, "POST " + MailEndpoint.Path + " HTTP/1.1",
                        StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, 404, "{\"error\":\"Unknown endpoint.\"}")
                        .ConfigureAwait(false);
                    return;
                }

                using var document = JsonDocument.Parse(body);
                if (!document.RootElement.TryGetProperty("envelope", out var envelopeElement)
                    || envelopeElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("image", out var imageElement)
                    || imageElement.ValueKind != JsonValueKind.String)
                {
                    await WriteResponseAsync(stream, 400, "{\"error\":\"The delivery is incomplete.\"}")
                        .ConfigureAwait(false);
                    return;
                }

                var envelope = MailEnvelope.Parse(envelopeElement.GetRawText());
                var image = Convert.FromBase64String(imageElement.GetString() ?? "");
                var accepted = await _service.AcceptDeliveryAsync(envelope, image, cancellationToken)
                    .ConfigureAwait(false);
                await WriteResponseAsync(stream, accepted ? 200 : 409,
                    accepted ? "{\"ok\":true}" : "{\"error\":\"The message is already stored.\"}")
                    .ConfigureAwait(false);
            }
            catch (MailServiceException ex)
            {
                try
                {
                    await WriteResponseAsync(client.GetStream(),
                        (int) (ex.Status ?? HttpStatusCode.BadRequest),
                        JsonSerializer.Serialize(new { error = ex.Message })).ConfigureAwait(false);
                }
                catch (Exception) { }
            }
            catch (Exception ex)
            {
                try
                {
                    await WriteResponseAsync(client.GetStream(), 400,
                        JsonSerializer.Serialize(new { error = ex.Message })).ConfigureAwait(false);
                }
                catch (Exception) { }
            }
        }
    }

    private static async Task<(string RequestLine, byte[] Body)> ReadRequestAsync(
        NetworkStream stream, CancellationToken cancellationToken)
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
        var firstLine = header.Split("\r\n", StringSplitOptions.None)[0];
        var contentLength = 0;
        foreach (var line in header.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[15..].Trim(), out var length))
                contentLength = length;
        if (contentLength <= 0 || contentLength > MaxRequestBytes)
            throw new InvalidDataException("The mail request has an invalid size.");

        var bodyStart = headerEnd + 4;
        var body = new byte[contentLength];
        var already = all.Length - bodyStart;
        if (already > 0) Buffer.BlockCopy(all, bodyStart, body, 0, Math.Min(already, contentLength));
        var offset = Math.Min(already, contentLength);
        while (offset < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
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

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string json)
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
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header)).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
    }

    public void Dispose() => Stop();
}

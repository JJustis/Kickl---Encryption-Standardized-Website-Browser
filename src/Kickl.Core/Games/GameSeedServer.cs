using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace Kickl.Core.Games;

/// <summary>
/// Lets this computer hand a game it holds to another computer, so games spread from whoever has them. It answers exactly one request,
/// <c>GET /v1/game/&lt;sha256&gt;</c>, with that game's file or a 404, over TLS 1.3 with this computer's own key (which the address it
/// advertises pins). It is exposed on every interface, so it is built for hostile peers like the mail listener: a cap on simultaneous
/// connections, a handshake timeout, a hard per-connection deadline, a tiny request limit, and nothing it will serve except a game this
/// computer holds. The receiver checks the bytes against the game's signed card, so a lying peer gains nothing.
/// </summary>
public sealed partial class GameSeedServer : IDisposable
{
    public const int MaxConnections = 12;

    [GeneratedRegex("^GET /v1/game/([0-9a-f]{64}) HTTP/1\\.1$")]
    private static partial Regex RequestLine();

    private readonly X509Certificate2 _certificate;
    private readonly Func<string, byte[]?> _lookup;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private Task? _loop;
    private bool _disposed;

    /// <summary>The longest one connection may live. Tests shorten it.</summary>
    internal TimeSpan ConnectionDeadline { get; set; } = TimeSpan.FromSeconds(30);

    /// <param name="certificate">Owned by the caller; this server does not dispose it.</param>
    /// <param name="lookup">The game file for a SHA-256, or null when this computer does not hold it.</param>
    public GameSeedServer(X509Certificate2 certificate, int port, Func<string, byte[]?> lookup)
    {
        _certificate = certificate;
        _lookup = lookup;
        _listener = new TcpListener(IPAddress.Any, port);
        Port = port;
    }

    public int Port { get; private set; }
    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null) return;
        _listener.Start();
        Port = ((IPEndPoint) _listener.LocalEndpoint).Port;
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { return; }

            if (!_slots.Wait(0)) { client.Dispose(); continue; }   // full: drop the newcomer at once
            _ = HandleAsync(client, token).ContinueWith(_ => _slots.Release(), TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken serverToken)
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

                var request = await ReadRequestLineAsync(tls, token).ConfigureAwait(false);
                var match = RequestLine().Match(request);
                var body = match.Success ? _lookup(match.Groups[1].Value) : null;
                await RespondAsync(tls, body, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or InvalidDataException
                                           or SocketException or ObjectDisposedException)
            {
                // a failed handshake, a silent or slow peer, or a malformed request: close without a word
            }
            finally
            {
                if (tls is not null) await tls.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Read up to the end of the headers (a request has no body here) and return the first line. Nothing larger than 8 KiB is read.</summary>
    private static async Task<string> ReadRequestLineAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        var used = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used), token).ConfigureAwait(false);
            if (read <= 0) throw new InvalidDataException("The request ended early.");
            used += read;
            var text = Encoding.ASCII.GetString(buffer, 0, used);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
            if (used >= buffer.Length) throw new InvalidDataException("The request is too large.");
        }
    }

    private static async Task RespondAsync(Stream stream, byte[]? body, CancellationToken token)
    {
        var found = body is not null;
        var header = found
            ? $"HTTP/1.1 200 OK\r\nContent-Type: application/wasm\r\nContent-Length: {body!.Length}\r\nConnection: close\r\n\r\n"
            : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token).ConfigureAwait(false);
        if (found) await stream.WriteAsync(body!, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _listener.Stop(); } catch (SocketException) { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }
}

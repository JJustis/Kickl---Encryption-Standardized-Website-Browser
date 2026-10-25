using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kickl.Core.Ownership;

namespace Kickl.Device;

/// <summary>A Wi-Fi discovery hint. The advertised receiver ID is untrusted until signed hello validation.</summary>
public sealed record WifiDongleEndpoint(IPAddress Address, int Port, string ReceiverIdHint);

/// <summary>
/// Driverless Wi-Fi transport for direct dongle links and existing LANs. Discovery is only a hint;
/// all receiver identity decisions are made by signed, challenge-bound hello and acknowledgment frames.
/// </summary>
public sealed class WifiDongleTransport : IDongleTransport
{
    public const int DiscoveryPort = 39017;
    public const int ReceiverPort = 39018;
    public const string DiscoveryGroup = "239.255.42.99";
    private static readonly byte[] ProbeMagic = "KDW1"u8.ToArray();
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(10);
    private readonly WifiDongleEndpoint _endpoint;
    private readonly string? _pinnedReceiverId;
    private readonly string? _pinnedPublicKeySpki;
    private string? _receiverPublicKeySpki;
    private string? _verifiedFingerprint;
    private bool _fingerprintConfirmed;

    public string ReceiverId { get; private set; }

    public void ConfirmFingerprint(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(_verifiedFingerprint) ||
            !string.Equals(_verifiedFingerprint, fingerprint, StringComparison.Ordinal))
            throw new CryptographicException("The confirmed fingerprint does not match the latest authenticated hello.");
        _fingerprintConfirmed = true;
    }

    public WifiDongleTransport(WifiDongleEndpoint endpoint, PairedDonglePinRecord? pinnedIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Address.AddressFamily != AddressFamily.InterNetwork || endpoint.Port is < 1 or > 65535)
            throw new ArgumentException("Wi-Fi dongle endpoint is invalid.", nameof(endpoint));
        _endpoint = endpoint;
        _pinnedReceiverId = pinnedIdentity?.ReceiverId;
        _pinnedPublicKeySpki = pinnedIdentity?.RsaPublicKeySpki;
        _receiverPublicKeySpki = _pinnedPublicKeySpki;
        _fingerprintConfirmed = pinnedIdentity is not null;
        ReceiverId = _pinnedReceiverId ?? endpoint.ReceiverIdHint;
    }

    /// <summary>
    /// Multicast discovery for both dongle-hosted direct networks and existing LANs. Results are
    /// untrusted and must be followed by the signed hello and explicit fingerprint confirmation.
    /// </summary>
    public static async Task<IReadOnlyList<WifiDongleEndpoint>> DiscoverAsync(
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var duration = timeout ?? TimeSpan.FromSeconds(2);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Discovery duration must be between 0 and 10 seconds.");

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var nonce = RandomNumberGenerator.GetBytes(16);
        var probe = new byte[ProbeMagic.Length + nonce.Length];
        ProbeMagic.CopyTo(probe, 0);
        nonce.CopyTo(probe, ProbeMagic.Length);
        await udp.SendAsync(probe, new IPEndPoint(IPAddress.Parse(DiscoveryGroup), DiscoveryPort), cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(duration);
        var found = new Dictionary<string, WifiDongleEndpoint>(StringComparer.Ordinal);
        while (!timeoutSource.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try { datagram = await udp.ReceiveAsync(timeoutSource.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
            if (!TryParseAdvertisement(datagram.Buffer, nonce, datagram.RemoteEndPoint.Address, out var endpoint))
                continue;
            found[$"{endpoint.Address}:{endpoint.Port}:{endpoint.ReceiverIdHint}"] = endpoint;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return found.Values.OrderBy(e => e.Address.ToString(), StringComparer.Ordinal)
            .ThenBy(e => e.ReceiverIdHint, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Advertisement: KDW1 | probe nonce (16) | TCP port (u16 BE) | receiver ID (1..64 UTF-8 bytes).</summary>
    public static byte[] CreateAdvertisement(ReadOnlySpan<byte> probeNonce, int tcpPort, string receiverId)
    {
        if (probeNonce.Length != 16) throw new ArgumentException("Discovery nonce must be 16 bytes.", nameof(probeNonce));
        if (tcpPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(tcpPort));
        var id = Encoding.UTF8.GetBytes(receiverId);
        if (id.Length is < 1 or > 64) throw new ArgumentException("Receiver ID must be 1 to 64 UTF-8 bytes.", nameof(receiverId));
        var result = new byte[ProbeMagic.Length + 16 + 2 + 1 + id.Length];
        ProbeMagic.CopyTo(result, 0);
        probeNonce.CopyTo(result.AsSpan(4));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(20), (ushort)tcpPort);
        result[22] = (byte)id.Length;
        id.CopyTo(result, 23);
        return result;
    }

    private static bool TryParseAdvertisement(byte[] data, byte[] nonce, IPAddress address,
        out WifiDongleEndpoint endpoint)
    {
        endpoint = null!;
        if (data.Length < 24 || data.Length > 87 || !data.AsSpan(0, 4).SequenceEqual(ProbeMagic) ||
            !CryptographicOperations.FixedTimeEquals(data.AsSpan(4, 16), nonce)) return false;
        var port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(20, 2));
        var idLength = data[22];
        if (port == 0 || idLength is < 1 or > 64 || data.Length != 23 + idLength) return false;
        string id;
        try { id = new UTF8Encoding(false, true).GetString(data, 23, idLength); }
        catch (DecoderFallbackException) { return false; }
        endpoint = new WifiDongleEndpoint(address, port, id);
        return true;
    }

    public async Task<DongleFrame> ExchangeAsync(DongleFrame frame, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExchangeTimeout);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(_endpoint.Address, _endpoint.Port, timeout.Token);
        await using var stream = client.GetStream();
        await DongleFrameCodec.WriteAsync(stream, frame, timeout.Token);
        var response = await DongleFrameCodec.ReadAsync(stream, timeout.Token);
        ValidateExchange(frame, response);
        return response;
    }

    private void ValidateExchange(DongleFrame request, DongleFrame response)
    {
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        if (request.Type == DongleFrameType.ReceiverHelloRequest)
        {
            if (response.Type != DongleFrameType.ReceiverHelloResponse)
                throw new InvalidDataException("Wi-Fi dongle returned the wrong hello response type.");
            var challenge = JsonSerializer.Deserialize<PairedDongleHelloRequest>(request.Payload, json)
                            ?? throw new InvalidDataException("Wi-Fi dongle hello challenge is unreadable.");
            var hello = JsonSerializer.Deserialize<PairedDongleHello>(response.Payload, json)
                        ?? throw new InvalidDataException("Wi-Fi dongle identity is unreadable.");
            _verifiedFingerprint = OwnershipAuthority.VerifyPairedDongleHello(hello, challenge.Challenge);
            if ((_pinnedReceiverId is not null && _pinnedReceiverId != hello.ReceiverId) ||
                (_pinnedPublicKeySpki is not null && _pinnedPublicKeySpki != hello.RsaPublicKeySpki) ||
                (!string.IsNullOrWhiteSpace(_endpoint.ReceiverIdHint) && _endpoint.ReceiverIdHint != hello.ReceiverId))
                throw new CryptographicException("Wi-Fi dongle identity differs from the selected or pinned receiver.");
            ReceiverId = hello.ReceiverId;
            _receiverPublicKeySpki = hello.RsaPublicKeySpki;
            return;
        }

        if (request.Type == DongleFrameType.OwnershipProofRequest)
        {
            if (!_fingerprintConfirmed || string.IsNullOrWhiteSpace(_receiverPublicKeySpki) ||
                (_pinnedPublicKeySpki is not null && _receiverPublicKeySpki != _pinnedPublicKeySpki))
                throw new CryptographicException("Pair and pin the Wi-Fi receiver before sending a proof.");
            if (response.Type != DongleFrameType.OwnershipProofResponse)
                throw new InvalidDataException("Wi-Fi dongle returned the wrong proof response type.");
            var ack = JsonSerializer.Deserialize<PairedDongleAck>(response.Payload, json)
                      ?? throw new InvalidDataException("Wi-Fi dongle acknowledgment is unreadable.");
            if (ack.ReceiverId != ReceiverId || !OwnershipAuthority.VerifyPairedDongleAck(_receiverPublicKeySpki, ack))
                throw new CryptographicException("Wi-Fi dongle acknowledgment signature is invalid.");
            return;
        }
        throw new InvalidDataException("Unsupported Wi-Fi dongle request type.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

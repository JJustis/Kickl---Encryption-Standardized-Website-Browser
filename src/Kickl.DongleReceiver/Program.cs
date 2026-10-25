using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Kickl.Core.Ownership;

namespace Kickl.DongleReceiver;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--stdio-receiver") return 2;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var identityLine = await Console.In.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(identityLine) || identityLine.Length > 32_000)
                throw new InvalidDataException("Receiver identity input is empty or too large.");
            var identity = JsonSerializer.Deserialize<PairedDongleIdentity>(identityLine, Json)
                           ?? throw new InvalidDataException("Receiver identity input is invalid.");

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            try
            {
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                Console.Out.WriteLine(JsonSerializer.Serialize(new ReadyMessage(identity.Id, endpoint.Port)));
                Console.Out.Flush();

                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                client.ReceiveTimeout = 10_000;
                client.SendTimeout = 10_000;
                await using var stream = client.GetStream();
                var frame = await DongleFrameCodec.ReadAsync(stream, timeout.Token);
                switch (frame.Type)
                {
                    case DongleFrameType.ReceiverHelloRequest:
                    {
                        var helloRequest = JsonSerializer.Deserialize<PairedDongleHelloRequest>(frame.Payload, Json)
                                           ?? throw new InvalidDataException("Receiver hello request is invalid.");
                        var hello = OwnershipAuthority.CreatePairedDongleHello(identity, helloRequest);
                        var response = JsonSerializer.SerializeToUtf8Bytes(hello);
                        await DongleFrameCodec.WriteAsync(stream,
                            new DongleFrame(DongleFrameType.ReceiverHelloResponse, response), timeout.Token);
                        return 0;
                    }
                    case DongleFrameType.OwnershipProofRequest:
                    {
                        var request = JsonSerializer.Deserialize<ProofRequest>(frame.Payload, Json)
                                      ?? throw new InvalidDataException("Pairing request is invalid.");
                        var proof = OwnershipAuthority.OpenAtPairedDongle(request.Packet, identity);
                        var valid = OwnershipAuthority.VerifyProof(request.Owner, proof);
                        var response = JsonSerializer.SerializeToUtf8Bytes(
                            OwnershipAuthority.CreatePairedDongleAck(identity, proof.Site, proof.Challenge, valid));
                        await DongleFrameCodec.WriteAsync(stream,
                            new DongleFrame(DongleFrameType.OwnershipProofResponse, response), timeout.Token);
                        return valid ? 0 : 3;
                    }
                    default:
                        throw new InvalidDataException("Unexpected dongle protocol frame.");
                }
            }
            finally
            {
                listener.Stop();
                identity.RsaPrivateKeyPkcs8 = "";
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private sealed record ReadyMessage(string ReceiverId, int Port);
    private sealed record ProofRequest(PairedDonglePacket Packet, OwnershipPublic Owner);
}

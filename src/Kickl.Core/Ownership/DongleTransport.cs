using System.Buffers.Binary;
using System.Text;

namespace Kickl.Core.Ownership;

public enum DongleFrameType : byte
{
    ReceiverHelloRequest = 1,
    ReceiverHelloResponse = 2,
    OwnershipProofRequest = 3,
    OwnershipProofResponse = 4
}

public sealed record DongleFrame(DongleFrameType Type, byte[] Payload, byte Version = 1);

/// <summary>
/// Transport boundary shared by loopback, USB/HID, Bluetooth, and Wi-Fi adapters. Implementations
/// exchange one bounded protocol frame and must not reinterpret or log its payload.
/// </summary>
public interface IDongleTransport : IAsyncDisposable
{
    string ReceiverId { get; }
    Task<DongleFrame> ExchangeAsync(DongleFrame frame, CancellationToken cancellationToken = default);
}

/// <summary>Versioned, length-bounded dongle frame format: KDL1 | version | type | uint32 length | payload.</summary>
public static class DongleFrameCodec
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("KDL1");
    public const int HeaderSize = 10;
    public const int MaxPayloadSize = 128 * 1024;

    public static async Task WriteAsync(Stream stream, DongleFrame frame, CancellationToken cancellationToken = default)
    {
        if (frame.Version != 1) throw new InvalidDataException("Unsupported dongle protocol version.");
        if (frame.Payload.Length > MaxPayloadSize) throw new InvalidDataException("Dongle frame exceeds the size limit.");
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[4] = frame.Version;
        header[5] = (byte)frame.Type;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(6), (uint)frame.Payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(frame.Payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<DongleFrame> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Dongle frame magic is invalid.");
        if (header[4] != 1) throw new InvalidDataException("Unsupported dongle protocol version.");
        if (!Enum.IsDefined(typeof(DongleFrameType), header[5]))
            throw new InvalidDataException("Unknown dongle frame type.");
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(6));
        if (length > MaxPayloadSize) throw new InvalidDataException("Dongle frame exceeds the size limit.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return new DongleFrame((DongleFrameType)header[5], payload, header[4]);
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using HidSharp;
using Kickl.Core.Ownership;

namespace Kickl.Device;

public sealed record UsbHidDongleDescriptor(
    int VendorId, int ProductId, string SerialNumber, string Manufacturer, string ProductName, string DevicePath);

/// <summary>
/// USB HID adapter for devices implementing the documented KHF1 report-fragment format.
/// VID/PID and the pinned receiver ID must be supplied explicitly; arbitrary HID devices are never opened.
/// </summary>
public sealed class UsbHidDongleTransport : IDongleTransport
{
    private static readonly byte[] FragmentMagic = "KHF1"u8.ToArray();
    private const int FragmentHeaderSize = 16;
    private const int MaxHidTransferSize = 64 * 1024;
    private const int MaxHidReportCount = 4096;
    private readonly HidStream _stream;
    private readonly byte _outputReportId;
    private readonly byte _inputReportId;
    private readonly int _outputReportSize;
    private readonly int _inputReportSize;
    private readonly string? _expectedReceiverId;
    private readonly string? _expectedPublicKeySpki;
    private string? _receiverPublicKeySpki;
    private int _disposed;

    public string ReceiverId { get; private set; }
    public string? ReceiverFingerprint { get; private set; }
    public UsbHidDongleDescriptor Device { get; }

    public static IReadOnlyList<UsbHidDongleDescriptor> Discover(int vendorId, int productId, string? serialNumber = null)
    {
        ValidateUsbId(vendorId, nameof(vendorId));
        ValidateUsbId(productId, nameof(productId));
        return DeviceList.Local.GetHidDevices(vendorId, productId, serialNumber: serialNumber)
            .Select(device => new UsbHidDongleDescriptor(device.VendorID, device.ProductID,
                device.GetSerialNumber() ?? "", device.GetManufacturer() ?? "", device.GetProductName() ?? "", device.DevicePath))
            .ToArray();
    }

    public UsbHidDongleTransport(UsbHidDongleDescriptor descriptor, PairedDonglePinRecord? pinnedIdentity = null,
        byte inputReportId = 0, byte outputReportId = 0)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateUsbId(descriptor.VendorId, nameof(descriptor.VendorId));
        ValidateUsbId(descriptor.ProductId, nameof(descriptor.ProductId));

        var device = DeviceList.Local.GetHidDevices(descriptor.VendorId, descriptor.ProductId,
                serialNumber: string.IsNullOrWhiteSpace(descriptor.SerialNumber) ? null : descriptor.SerialNumber)
            .SingleOrDefault(candidate => candidate.DevicePath == descriptor.DevicePath)
            ?? throw new IOException("The selected paired USB HID dongle is no longer connected.");
        if (!device.TryOpen(out var stream))
            throw new IOException("The selected USB HID dongle could not be opened.");

        Device = descriptor;
        _expectedReceiverId = pinnedIdentity?.ReceiverId;
        _expectedPublicKeySpki = pinnedIdentity?.RsaPublicKeySpki;
        _receiverPublicKeySpki = _expectedPublicKeySpki;
        ReceiverId = _expectedReceiverId ?? "";
        _stream = stream;
        _stream.ReadTimeout = 10_000;
        _stream.WriteTimeout = 10_000;
        _inputReportSize = device.GetMaxInputReportLength();
        _outputReportSize = device.GetMaxOutputReportLength();
        _inputReportId = inputReportId;
        _outputReportId = outputReportId;
        if (_inputReportSize < FragmentHeaderSize + 1 || _outputReportSize < FragmentHeaderSize + 1)
        {
            _stream.Dispose();
            throw new InvalidDataException("The selected dongle HID reports are too small for KHF1 framing.");
        }
    }

    public Task<DongleFrame> ExchangeAsync(DongleFrame frame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (frame.Version != 1) throw new InvalidDataException("Unsupported dongle protocol version.");
        var encoded = EncodeFrame(frame);
        if (encoded.Length > MaxHidTransferSize)
            throw new InvalidDataException("Dongle frame exceeds the USB HID transfer limit.");
        return Task.Run(() => ExchangeSynchronously(frame, encoded, cancellationToken), cancellationToken);
    }

    private DongleFrame ExchangeSynchronously(DongleFrame frame, byte[] encoded, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transferId = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
        WriteFragments(encoded, transferId, cancellationToken);
        var response = ReadFragments(transferId, cancellationToken);
        var decoded = DecodeFrame(response);
        ValidateExchange(frame, decoded);
        return decoded;
    }

    private void ValidateExchange(DongleFrame request, DongleFrame response)
    {
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        if (request.Type == DongleFrameType.ReceiverHelloRequest)
        {
            if (response.Type != DongleFrameType.ReceiverHelloResponse)
                throw new InvalidDataException("USB dongle returned the wrong hello response type.");
            var challenge = JsonSerializer.Deserialize<PairedDongleHelloRequest>(request.Payload, json)
                            ?? throw new InvalidDataException("USB dongle hello challenge is unreadable.");
            var hello = JsonSerializer.Deserialize<PairedDongleHello>(response.Payload, json)
                        ?? throw new InvalidDataException("USB dongle identity is unreadable.");
            var fingerprint = OwnershipAuthority.VerifyPairedDongleHello(hello, challenge.Challenge);
            if ((_expectedReceiverId is not null && _expectedReceiverId != hello.ReceiverId) ||
                (_expectedPublicKeySpki is not null && _expectedPublicKeySpki != hello.RsaPublicKeySpki))
                throw new CryptographicException("USB dongle identity differs from the pinned receiver.");
            ReceiverId = hello.ReceiverId;
            ReceiverFingerprint = fingerprint;
            _receiverPublicKeySpki = hello.RsaPublicKeySpki;
            return;
        }

        if (request.Type == DongleFrameType.OwnershipProofRequest)
        {
            if (string.IsNullOrWhiteSpace(_receiverPublicKeySpki))
                throw new CryptographicException("Authenticate and pin the USB receiver before sending a proof.");
            if (response.Type != DongleFrameType.OwnershipProofResponse)
                throw new InvalidDataException("USB dongle returned the wrong proof response type.");
            var ack = JsonSerializer.Deserialize<PairedDongleAck>(response.Payload, json)
                      ?? throw new InvalidDataException("USB dongle acknowledgment is unreadable.");
            if (ack.ReceiverId != ReceiverId || !OwnershipAuthority.VerifyPairedDongleAck(_receiverPublicKeySpki, ack))
                throw new CryptographicException("USB dongle acknowledgment signature is invalid.");
            return;
        }
        throw new InvalidDataException("Unsupported USB HID dongle request type.");
    }

    private void WriteFragments(byte[] frame, uint transferId, CancellationToken cancellationToken)
    {
        var chunkSize = _outputReportSize - 1 - FragmentHeaderSize;
        var count = checked((frame.Length + chunkSize - 1) / chunkSize);
        if (count > MaxHidReportCount) throw new InvalidDataException("HID transfer requires too many reports.");
        var report = new byte[_outputReportSize];
        report[0] = _outputReportId;
        FragmentMagic.CopyTo(report, 1);
        BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(5), transferId);
        BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(11), checked((ushort)count));
        BinaryPrimitives.WriteUInt32BigEndian(report.AsSpan(13), checked((uint)frame.Length));

        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = index * chunkSize;
            var bytes = Math.Min(chunkSize, frame.Length - offset);
            BinaryPrimitives.WriteUInt16BigEndian(report.AsSpan(9), checked((ushort)index));
            Array.Clear(report, 17, chunkSize);
            frame.AsSpan(offset, bytes).CopyTo(report.AsSpan(17));
            _stream.Write(report);
        }
    }

    private byte[] ReadFragments(uint expectedTransferId, CancellationToken cancellationToken)
    {
        var chunkSize = _inputReportSize - 1 - FragmentHeaderSize;
        var report = new byte[_inputReportSize];
        var received = new MemoryStream();
        ushort expectedCount = 0;
        uint expectedLength = 0;
        for (var expectedIndex = 0; ; expectedIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = _stream.Read(report);
            if (read < FragmentHeaderSize + 1 || report[0] != _inputReportId ||
                !report.AsSpan(1, FragmentMagic.Length).SequenceEqual(FragmentMagic))
                throw new InvalidDataException("Invalid HID dongle report or report ID.");
            if (BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(5)) != expectedTransferId)
                throw new InvalidDataException("HID dongle response has the wrong transfer identifier.");

            var index = BinaryPrimitives.ReadUInt16BigEndian(report.AsSpan(9));
            var count = BinaryPrimitives.ReadUInt16BigEndian(report.AsSpan(11));
            var totalLength = BinaryPrimitives.ReadUInt32BigEndian(report.AsSpan(13));
            if (count == 0 || count > MaxHidReportCount || totalLength > MaxHidTransferSize ||
                index != expectedIndex || (expectedIndex > 0 && (count != expectedCount || totalLength != expectedLength)))
                throw new InvalidDataException("HID dongle response fragment sequence is invalid.");
            if (expectedIndex == 0)
            {
                expectedCount = count;
                expectedLength = totalLength;
                var requiredCount = (expectedLength + (uint)chunkSize - 1) / (uint)chunkSize;
                if (requiredCount != count) throw new InvalidDataException("HID dongle response length/count mismatch.");
            }

            var chunkLength = (int)Math.Min((uint)chunkSize, expectedLength - (uint)received.Length);
            if (read < 17 + chunkLength) throw new InvalidDataException("HID dongle report ended early.");
            received.Write(report, 17, chunkLength);
            if (expectedIndex + 1 == expectedCount) break;
        }
        if (received.Length != expectedLength) throw new InvalidDataException("HID dongle response was truncated.");
        return received.ToArray();
    }

    private static byte[] EncodeFrame(DongleFrame frame)
    {
        using var stream = new MemoryStream();
        DongleFrameCodec.WriteAsync(stream, frame).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    private static DongleFrame DecodeFrame(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return DongleFrameCodec.ReadAsync(stream).GetAwaiter().GetResult();
    }

    private static void ValidateUsbId(int value, string name)
    {
        if (value is < 1 or > 0xffff) throw new ArgumentOutOfRangeException(name, "USB VID/PID must be 1..65535.");
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _stream.Dispose();
        return ValueTask.CompletedTask;
    }
}

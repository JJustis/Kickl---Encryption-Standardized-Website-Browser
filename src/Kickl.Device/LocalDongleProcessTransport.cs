using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Kickl.Core.Ownership;

namespace Kickl.Device;

/// <summary>Development transport that connects a trusted device to a separate local receiver process.</summary>
internal sealed class LocalDongleProcessTransport : IDongleTransport
{
    private readonly PairedDongleIdentity _identity;
    private Process? _process;

    public string ReceiverId => _identity.Id;

    public LocalDongleProcessTransport(PairedDongleIdentity identity) => _identity = identity;

    public async Task<DongleFrame> ExchangeAsync(DongleFrame frame,
        CancellationToken cancellationToken = default)
    {
        if (frame.Type is not (DongleFrameType.ReceiverHelloRequest or DongleFrameType.OwnershipProofRequest))
            throw new InvalidDataException("The local receiver accepts only hello and ownership proof requests.");
        if (_process is not null) throw new InvalidOperationException("A receiver exchange is already active.");

        var baseDirectory = AppContext.BaseDirectory;
        var receiverAssembly = Path.Combine(baseDirectory, "Kickl.DongleReceiver.dll");
        var receiverDeps = Path.Combine(baseDirectory, "Kickl.DongleReceiver.deps.json");
        var runtimeConfig = Path.Combine(baseDirectory, "Kickl.DongleReceiver.runtimeconfig.json");
        if (!File.Exists(receiverAssembly) || !File.Exists(runtimeConfig) || !File.Exists(receiverDeps))
            throw new FileNotFoundException("The paired receiver runtime is not installed beside the emulator.");

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = baseDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(runtimeConfig);
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(receiverDeps);
        start.ArgumentList.Add(receiverAssembly);
        start.ArgumentList.Add("--stdio-receiver");

        var process = Process.Start(start)
                      ?? throw new InvalidOperationException("Could not start the paired receiver process.");
        _process = process;
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(_identity));
            process.StandardInput.Close();
            var readyLine = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(readyLine))
                throw new InvalidOperationException("The paired receiver did not announce its local endpoint: " +
                    await stderrTask);
            var ready = JsonSerializer.Deserialize<ReceiverReady>(readyLine,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("The paired receiver endpoint response is invalid.");
            if (ready.Port is < 1 or > 65535 || ready.ReceiverId != _identity.Id)
                throw new CryptographicException("The paired receiver identity did not match its pinned identity.");

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ready.Port, cancellationToken);
            await using var stream = client.GetStream();
            await DongleFrameCodec.WriteAsync(stream, frame, cancellationToken);
            var response = await DongleFrameCodec.ReadAsync(stream, cancellationToken);
            var expectedType = frame.Type switch
            {
                DongleFrameType.ReceiverHelloRequest => DongleFrameType.ReceiverHelloResponse,
                DongleFrameType.OwnershipProofRequest => DongleFrameType.OwnershipProofResponse,
                _ => throw new InvalidDataException("Unsupported request type for the local receiver.")
            };
            if (response.Type != expectedType)
                throw new InvalidDataException("The paired receiver returned an unexpected frame type.");

            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("The paired receiver failed: " + await stderrTask);
            process.Dispose();
            _process = null;
            return response;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.Dispose();
            _process = null;
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            _process.Dispose();
            _process = null;
        }
        _identity.RsaPrivateKeyPkcs8 = "";
        return ValueTask.CompletedTask;
    }

    private sealed record ReceiverReady(string ReceiverId, int Port);
}

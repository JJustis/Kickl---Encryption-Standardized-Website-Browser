using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kickl.Core.Util;

namespace Kickl.Core.Mail;

/// <summary>
/// The transport key for direct device-to-device mail. Each device makes one self-signed TLS certificate and publishes a PIN of its
/// public key inside its signed mail endpoint (<c>https://host:port#pin=…</c>). There is no certificate authority on a LAN address,
/// so trust is the pin: the identity signature covers the endpoint string, so a sender knows exactly which key the recipient meant,
/// and a man in the middle cannot present a different one.
/// </summary>
public static class MailTls
{
    public const string PinPrefix = "#pin=";
    public const int PinLength = 43;   // base64url of a SHA-256 hash, no padding

    /// <summary>A fresh ECDSA P-256 certificate. Its validity dates are irrelevant: only the key is ever checked.</summary>
    public static X509Certificate2 CreateSelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=kickl-mail", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false));   // serverAuth
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        // Re-import so the private key is usable by TLS on every platform (Windows will not serve with an ephemeral in-memory key)
        return Import(Export(created));
    }

    // The blob only ever lives inside the encrypted mail identity; the fixed password just satisfies Windows' PKCS#12 handling
    private const string BlobPassword = "kickl-mail-tls";

    public static byte[] Export(X509Certificate2 certificate) => certificate.Export(X509ContentType.Pkcs12, BlobPassword);

    public static X509Certificate2 Import(byte[] pkcs12) =>
        X509CertificateLoader.LoadPkcs12(pkcs12, BlobPassword, X509KeyStorageFlags.Exportable);

    /// <summary>base64url(SHA-256(SubjectPublicKeyInfo)): 43 characters naming exactly one public key.</summary>
    public static string PinOf(X509Certificate certificate)
    {
        // Dispose only a copy we made; a caller's own certificate (the identity's) must stay usable
        var owned = certificate is X509Certificate2 ? null : new X509Certificate2(certificate);
        var cert = owned ?? (X509Certificate2) certificate;
        try { return B64u.Encode(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo())); }
        finally { owned?.Dispose(); }
    }

    /// <summary>Does the certificate a server presented carry the key that was pinned? Chain and host name are deliberately ignored.</summary>
    public static bool PinMatches(X509Certificate? presented, string expectedPin)
    {
        if (presented is null || expectedPin.Length != PinLength) return false;
        var actual = System.Text.Encoding.ASCII.GetBytes(PinOf(presented));
        var expected = System.Text.Encoding.ASCII.GetBytes(expectedPin);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>The advertised address of a device's mail listener: always https, always with the pin of its TLS key.</summary>
public static class MailEndpoint
{
    public const string Path = "/v1/mail/deliver";

    /// <summary>This computer's LAN address on a chosen port, without a pin yet.</summary>
    public static string LocalBaseOn(int port) => $"https://{LocalAddress()}:{port}";

    /// <summary>A TCP port nothing is listening on right now.</summary>
    public static int PickFreePort() => FreePort();

    /// <summary>The LAN address and a free port, without a pin yet. A device behind NAT must forward the port or publish its public host.</summary>
    public static string LocalBase() => $"https://{LocalAddress()}:{PreferredPort()}";

    /// <summary>The port a new mail endpoint uses unless another program already holds it.</summary>
    public const int DefaultPort = 25;

    /// <summary>Set by the tests, which run many mail services at once and cannot share one port.</summary>
    internal static bool UseFreePortInstead { get; set; }

    /// <summary>The default port when it can be bound here, else a free one.</summary>
    public static int PreferredPort()
    {
        if (UseFreePortInstead) return FreePort();
        try
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Any, DefaultPort);
            probe.Start();
            probe.Stop();
            return DefaultPort;
        }
        catch (System.Net.Sockets.SocketException) { return FreePort(); }
    }

    /// <summary>
    /// Make <paramref name="endpoint"/> the canonical secure form for this device: https, no path, and this device's pin.
    /// Whatever scheme or fragment was typed is replaced; a person cannot publish an unpinned or plain-http endpoint.
    /// </summary>
    public static string WithPin(string endpoint, string pin)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The mail endpoint must be an address like host:port.", nameof(endpoint));
        var afterScheme = endpoint.Trim()[(endpoint.Trim().IndexOf("://", StringComparison.Ordinal) + 3)..];
        var authority = afterScheme.Split('/', '#', '?')[0];
        if (!authority.Contains(':', StringComparison.Ordinal) || authority.EndsWith(':'))
            throw new ArgumentException("The mail endpoint needs a port.", nameof(endpoint));
        if (pin.Length != MailTls.PinLength) throw new ArgumentException("That is not a TLS pin.", nameof(pin));
        var port = uri.Port is < 1 or > 65535 ? throw new ArgumentException("The mail endpoint has no usable port.", nameof(endpoint)) : uri.Port;
        return $"https://{uri.Host}:{port}{MailTls.PinPrefix}{pin}";
    }

    /// <summary>Split a published endpoint into where to connect and which key to demand. Plain http, or no pin, is refused.</summary>
    public static (Uri Address, string Pin) Secure(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new FormatException("The recipient mail endpoint is invalid.");
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new FormatException(
                "The recipient advertises an old, unencrypted mail endpoint (http). Mail is only delivered over TLS; ask them to update Kickl and republish.");
        if (!uri.Fragment.StartsWith(MailTls.PinPrefix, StringComparison.Ordinal)
            || uri.Fragment.Length != MailTls.PinPrefix.Length + MailTls.PinLength
            || !uri.Fragment[MailTls.PinPrefix.Length..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new FormatException("The recipient's mail endpoint carries no valid TLS key pin, so it cannot be trusted.");
        return (new Uri(uri.GetLeftPart(UriPartial.Authority)), uri.Fragment[MailTls.PinPrefix.Length..]);
    }

    public static int PortOf(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Port is < 1 or > 65535)
            throw new FormatException("The mail endpoint has no usable port.");
        return uri.Port;
    }

    /// <summary>The same endpoint on this computer (two profiles on one machine). The pin is kept: it is the key that matters, not the address.</summary>
    public static string Loopback(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new FormatException("The recipient mail endpoint is invalid.");
        return new UriBuilder(uri) { Host = "127.0.0.1" }.Uri.ToString().TrimEnd('/');
    }

    public static string DeliveryUrl(string endpoint)
    {
        var (address, _) = Secure(endpoint);
        return new Uri(address, Path).ToString();
    }

    private static IPAddress LocalAddress()
    {
        try
        {
            foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                    return address;
        }
        catch (System.Net.Sockets.SocketException) { }
        return IPAddress.Loopback;
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

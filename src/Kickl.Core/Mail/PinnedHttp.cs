using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Kickl.Core.Mail;

/// <summary>
/// An HTTP client for talking straight to one computer: TLS 1.3 only, and the ONLY thing it checks is that the server holds the key the
/// address named (<c>https://host:port#pin=…</c>). The certificate chain and host name mean nothing for a device on a LAN address, so the
/// pin is the whole trust decision. No proxy, no redirects. Used for direct mail delivery and for downloading games from another computer.
/// </summary>
public static class PinnedHttp
{
    public static HttpClient Create(string pin, TimeSpan timeout, long maxResponseBytes)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, certificate, _, _) => MailTls.PinMatches(certificate, pin)
            }
        };
        return new HttpClient(handler) { Timeout = timeout, MaxResponseContentBufferSize = maxResponseBytes };
    }
}

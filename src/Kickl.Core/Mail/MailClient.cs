using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kickl.Core.Util;

namespace Kickl.Core.Mail;

public sealed class MailServiceException(string message, HttpStatusCode? status = null,
    Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Pinwheel is the directory only: it stores a signed public bundle (RSA + Dilithium
/// public keys and the device endpoint) under the eight-digit PFS id. Mail itself is
/// posted straight to the recipient's listener and never stored on the server.
/// </summary>
public sealed class MailClient : IDisposable
{
    public const string DefaultBase = "https://secupgrade.com/apicalls";
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly bool _ownsHttp;

    public MailClient(string? baseUrl = null, HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = (http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) }).Limit(HttpLimits.Mail);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("KicklMail/0.1");
        var baseValue = baseUrl ?? DefaultBase;
        _baseUri = new Uri(baseValue.EndsWith('/') ? baseValue : baseValue + "/", UriKind.Absolute);
    }

    public async Task RegisterAsync(MailIdentityBundle bundle, string token,
        CancellationToken cancellationToken = default)
    {
        var tokenHash = Hashing.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        using var response = await PostJsonAsync(new Uri(_baseUri, "v1/mail/identity"),
            new { bundle, tokenHash }, cancellationToken).ConfigureAwait(false);
        await ReadObjectAsync(response, "identity", cancellationToken).ConfigureAwait(false);
    }

    public async Task<MailIdentityBundle> LookupAsync(string shortId,
        CancellationToken cancellationToken = default)
    {
        var normalized = new string(shortId.Where(char.IsAsciiDigit).ToArray());
        if (normalized.Length != 8)
            throw new ArgumentException("A PFS ID is exactly eight digits.", nameof(shortId));

        using var response = await _http.GetAsync(
            new Uri(_baseUri, "v1/mail/lookup?short=" + normalized), cancellationToken)
            .ConfigureAwait(false);
        using var document = await ParseAsync(response, "lookup", cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("bundle", out var bundleElement)
            || bundleElement.ValueKind != JsonValueKind.Object)
            throw new MailServiceException("The Pinwheel lookup did not return a mail identity.",
                response.StatusCode);

        var bundle = bundleElement.Deserialize<MailIdentityBundle>(MailJson.Options)
                     ?? throw new MailServiceException("The Pinwheel mail identity is unreadable.");
        bundle.Validate();
        return bundle;
    }

    /// <summary>POST the sealed image straight to the recipient's advertised device.</summary>
    public async Task DeliverAsync(MailIdentityBundle recipient, MailEnvelope envelope,
        byte[] image, CancellationToken cancellationToken = default)
    {
        if (image.Length > MailCrypto.MaxImageBytes)
            throw new ArgumentException("The sealed mail image is too large.", nameof(image));
        if (string.IsNullOrWhiteSpace(recipient.Endpoint))
            throw new MailServiceException("The recipient has not advertised a direct mail endpoint.");

        Uri address;
        string pin;
        try { (address, pin) = MailEndpoint.Secure(recipient.Endpoint); }
        catch (FormatException ex) { throw new MailServiceException(ex.Message, null, ex); }

        // A client of its own for each delivery: TLS 1.3 only, and the ONLY thing it checks is that the server holds the key the recipient
        // published. The certificate chain and host name mean nothing for a device on a LAN address; the pin is the whole trust decision.
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, certificate, _, _) => MailTls.PinMatches(certificate, pin)
            }
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 64 * 1024 };

        using var response = await PostJsonAsync(http, new Uri(address, MailEndpoint.Path),
            new
            {
                envelope,
                image = Convert.ToBase64String(image),
                size = image.Length
            }, cancellationToken).ConfigureAwait(false);
        await ReadObjectAsync(response, "deliver", cancellationToken).ConfigureAwait(false);
    }

    private Task<HttpResponseMessage> PostJsonAsync(Uri uri, object body, CancellationToken cancellationToken) =>
        PostJsonAsync(_http, uri, body, cancellationToken);

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient http, Uri uri, object body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Content = new StringContent(JsonSerializer.Serialize(body, MailJson.Options),
            Encoding.UTF8, "application/json");
        return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response, string action,
        CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new MailServiceException(
                $"The mail endpoint returned an unreadable response for {action} ({(int) response.StatusCode}).",
                response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            using (document)
            {
                var error = document.RootElement.TryGetProperty("error", out var value)
                    ? value.GetString()
                    : null;
                throw new MailServiceException(
                    error ?? $"The mail endpoint returned {(int) response.StatusCode} for {action}.",
                    response.StatusCode);
            }
        }
        return document;
    }

    private static async Task ReadObjectAsync(HttpResponseMessage response, string action,
        CancellationToken cancellationToken)
    {
        using var document = await ParseAsync(response, action, cancellationToken).ConfigureAwait(false);
        _ = document.RootElement.ValueKind;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

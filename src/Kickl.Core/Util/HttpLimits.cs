namespace Kickl.Core.Util;

/// <summary>
/// .NET buffers up to 2 GB of a response by default, so a hostile or compromised server can make a client allocate until it
/// dies. Every client that talks to a server this browser does not control gets a ceiling that fits what it legitimately reads.
/// </summary>
public static class HttpLimits
{
    public const long CaAndShop = 1L << 20;          // 1 MiB: seals, revocation lists, a public key
    public const long Registry = 32L << 20;          // 32 MiB: a full signed phonebook snapshot
    public const long Chat = 8L << 20;               // 8 MiB: a chat reply, which may carry a sealed PNG
    public const long Mail = 8L << 20;               // 8 MiB: a mail reply
    public const long Feed = 2L << 20;               // 2 MiB: a news feed

    /// <summary>Lower the response ceiling. A client that has already made a request keeps what it had.</summary>
    public static HttpClient Limit(this HttpClient http, long maxBytes)
    {
        try
        {
            if (http.MaxResponseContentBufferSize > maxBytes) http.MaxResponseContentBufferSize = maxBytes;
        }
        catch (InvalidOperationException)
        {
            // already used: the caller built it before we could cap it
        }
        return http;
    }
}

using System.Net;
using System.Net.Sockets;

namespace Kickl.Core.Mail;

public static class MailEndpoint
{
    public const string Path = "/v1/mail/deliver";

    /// <summary>
    /// Advertise a LAN address and a free port. A user behind NAT has to forward that
    /// port or replace the endpoint with their public host before a remote device can
    /// deliver directly.
    /// </summary>
    public static string Local()
    {
        var address = LocalAddress();
        var port = FreePort();
        return $"http://{address}:{port}";
    }

    public static int PortOf(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Port is < 1 or > 65535)
            throw new FormatException("The mail endpoint has no usable port.");
        return uri.Port;
    }

    public static string Loopback(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new FormatException("The recipient mail endpoint is invalid.");
        return new UriBuilder(uri) { Host = "127.0.0.1" }.Uri.ToString().TrimEnd('/');
    }

    public static string DeliveryUrl(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new FormatException("The recipient mail endpoint is invalid.");
        return new Uri(uri, Path).ToString();
    }

    private static IPAddress LocalAddress()
    {
        try
        {
            foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
                if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                    return address;
        }
        catch (SocketException) { }
        return IPAddress.Loopback;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

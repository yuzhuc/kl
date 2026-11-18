using System.IO.Pipes;
using System.Net.Sockets;

namespace Kl.Client;

/// <summary>
/// Opens the local core transport. Windows uses a named pipe, Unix and macOS
/// use a Unix domain socket. The endpoint string is the pipe name on Windows
/// and the socket path elsewhere.
/// </summary>
public static class LocalSocketConnection
{
    public static Stream Connect(string endpoint)
    {
        if (TryParseTcp(endpoint, out var host, out var port))
        {
            var client = new TcpClient();
            try
            {
                client.Connect(host, port);
                client.NoDelay = true;
                return client.GetStream();
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var pipeName = endpoint.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)
                ? endpoint[8..]
                : endpoint;
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(5000);
            return pipe;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(endpoint));
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Recognises <c>tcp://host:port</c> and bare <c>host:port</c>. Pipe names
    /// and Unix socket paths stay on the local transport.
    /// </summary>
    public static bool TryParseTcp(string? endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var value = endpoint.Trim();
        var explicitScheme = value.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase);
        if (explicitScheme)
        {
            value = value[6..];
        }
        else
        {
            if (value.Contains('/') || value.Contains('\\'))
            {
                return false;
            }
            var colon = value.LastIndexOf(':');
            if (colon <= 0
                || colon == value.Length - 1
                || !value[(colon + 1)..].All(char.IsAsciiDigit))
            {
                return false;
            }
        }

        string hostPart;
        string portPart;
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']');
            if (end < 0 || end + 2 > value.Length || value[end + 1] != ':')
            {
                return false;
            }
            hostPart = value[1..end];
            portPart = value[(end + 2)..];
        }
        else
        {
            var colon = value.LastIndexOf(':');
            if (colon <= 0 || colon == value.Length - 1)
            {
                return false;
            }
            hostPart = value[..colon];
            portPart = value[(colon + 1)..];
        }

        if (hostPart.Length == 0
            || !int.TryParse(portPart, out port)
            || port is < 1 or > 65535)
        {
            return false;
        }

        host = hostPart;
        return true;
    }
}

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
}

using System.IO.Pipes;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kl.Client;

/// <summary>
/// Opens the local core transport. Windows uses a named pipe, Unix and macOS
/// use a Unix domain socket. The endpoint string is the pipe name on Windows
/// and the socket path elsewhere.
/// </summary>
public static class LocalSocketConnection
{
    public static Stream Connect(string endpoint, CancellationToken cancellationToken = default) =>
        Connect(endpoint, tlsFingerprint: null, cancellationToken);

    /// <summary>
    /// Connects to <c>tcp://</c>, <c>tls://</c> or a local transport. A
    /// <c>tls://</c> endpoint requires <paramref name="tlsFingerprint"/>: the
    /// leaf certificate's SHA-256 (with or without colons).
    /// </summary>
    public static Stream Connect(
        string endpoint,
        string? tlsFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (TryParseTcp(endpoint, out var host, out var port))
        {
            var usesTls = endpoint.TrimStart().StartsWith("tls://", StringComparison.OrdinalIgnoreCase);
            if (usesTls && string.IsNullOrWhiteSpace(tlsFingerprint))
            {
                throw new InvalidOperationException(
                    "tls:// endpoints require the server's SHA-256 certificate fingerprint");
            }

            var client = new TcpClient();
            try
            {
                client.ConnectAsync(host, port, cancellationToken).GetAwaiter().GetResult();
                client.NoDelay = true;
                if (!usesTls)
                {
                    return client.GetStream();
                }

                var expected = NormalizeFingerprint(tlsFingerprint!);
                var ssl = new SslStream(
                    client.GetStream(),
                    leaveInnerStreamOpen: false,
                    (_, certificate, _, _) =>
                        certificate is not null
                        && string.Equals(
                            Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())),
                            expected,
                            StringComparison.OrdinalIgnoreCase));
                ssl.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = host,
                            EnabledSslProtocols = SslProtocols.None
                        },
                        cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                return ssl;
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
            try
            {
                pipe.ConnectAsync(5000, cancellationToken).GetAwaiter().GetResult();
                return pipe;
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cancellationToken)
                .GetAwaiter().GetResult();
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Recognises <c>tcp://host:port</c>, <c>tls://host:port</c> and bare
    /// <c>host:port</c>. Pipe names and Unix socket paths stay local.
    /// </summary>
    /// <summary>Uppercase hex without separators; accepts colons and spaces.</summary>
    public static string NormalizeFingerprint(string fingerprint) =>
        fingerprint.Trim().Replace(":", string.Empty)
            .Replace("-", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();

    public static bool TryParseTcp(string? endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var value = endpoint.Trim();
        var explicitScheme = value.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("tls://", StringComparison.OrdinalIgnoreCase);
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

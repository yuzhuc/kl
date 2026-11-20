using Kl.Client;

namespace Kl.Gui.Tests;

public sealed class RemoteEndpointTests
{
    [Theory]
    [InlineData("tcp://127.0.0.1:8787", "127.0.0.1", 8787)]
    [InlineData("192.168.1.10:9000", "192.168.1.10", 9000)]
    [InlineData("tcp://[::1]:8787", "::1", 8787)]
    [InlineData("tcp://build-box:7000", "build-box", 7000)]
    public void TcpEndpointsParseHostAndPort(string endpoint, string host, int port)
    {
        Assert.True(LocalSocketConnection.TryParseTcp(endpoint, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("tls://127.0.0.1:8788", "127.0.0.1", 8788)]
    [InlineData("TLS://build-box:7001", "build-box", 7001)]
    public void TlsEndpointsParseHostAndPort(string endpoint, string host, int port)
    {
        Assert.True(LocalSocketConnection.TryParseTcp(endpoint, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Fact]
    public void TlsEndpointWithoutFingerprintIsRejectedBeforeConnecting()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => LocalSocketConnection.Connect("tls://127.0.0.1:1", tlsFingerprint: null));
        Assert.Contains("fingerprint", error.Message);
    }

    [Theory]
    [InlineData("ab:cd:ef")]
    [InlineData("AB CD EF")]
    [InlineData("ab-cd-ef")]
    public void FingerprintsIgnoreSeparatorsAndCase(string fingerprint)
    {
        Assert.Equal("ABCDEF", LocalSocketConnection.NormalizeFingerprint(fingerprint));
    }

    [Theory]
    [InlineData("kl-core")]
    [InlineData("/tmp/kl-core.sock")]
    [InlineData(@"\.\pipe\kl-core")]
    [InlineData("tcp://host:notaport")]
    [InlineData("tcp://host:70000")]
    [InlineData("")]
    public void LocalTransportsAreNotTcp(string endpoint)
    {
        Assert.False(LocalSocketConnection.TryParseTcp(endpoint, out _, out _));
    }
}

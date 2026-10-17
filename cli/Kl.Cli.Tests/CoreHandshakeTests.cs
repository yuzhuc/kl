using Xunit;

namespace Kl.Cli.Tests;

/// <summary>
/// Live process contract: the .NET client must be able to launch the Rust core
/// and complete handshake + ping. Build the core (cargo build) before
/// `dotnet test`.
/// </summary>
public sealed class CoreHandshakeTests
{
    [Fact]
    public async Task LaunchesCoreAndCompletesHandshakeAndPing()
    {
        var home = Path.Combine(Path.GetTempPath(), "kl-core-e2e-" + Guid.NewGuid().ToString("N"));
        var options = new CliOptions { HomePath = home };
        try
        {
            await using var client = KlCoreClient.Start(options);
            var handshake = await client.HandshakeAsync();
            Assert.Equal(Protocol.CoreName, handshake.ServerName);
            Assert.Equal(Protocol.Version, handshake.ProtocolVersion);

            var ping = await client.PingAsync("dotnet-test");
            Assert.True(ping.GetProperty("pong").GetBoolean());
            Assert.Equal("dotnet-test", ping.GetProperty("nonce").GetString());
        }
        finally
        {
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }
}

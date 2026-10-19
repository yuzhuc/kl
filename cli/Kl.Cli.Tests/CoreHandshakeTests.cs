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
    public async Task SurfacesCoreConfigurationErrors()
    {
        var root = Path.Combine(Path.GetTempPath(), "kl-core-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, "kl.toml");
        await File.WriteAllTextAsync(config, """
            [agent]
            provider = "missing"
            model = "m"

            [providers.missing]
            enabled = true
            path = "providers/missing.toml"
            """);
        try
        {
            await using var client = KlCoreClient.Start(new KlClientOptions
            {
                ConfigPath = config,
                HomePath = Path.Combine(root, "home")
            });
            var error = await Assert.ThrowsAsync<IOException>(() => client.HandshakeAsync());
            Assert.Contains("config error", error.Message);
            Assert.Contains("missing", error.Message);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LaunchesCoreAndCompletesHandshakeAndPing()
    {
        var home = Path.Combine(Path.GetTempPath(), "kl-core-e2e-" + Guid.NewGuid().ToString("N"));
        var options = new CliOptions { HomePath = home };
        try
        {
            await using var client = KlCoreClient.Start(options.ToClientOptions());
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

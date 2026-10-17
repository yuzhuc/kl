using System.Text.Json;
using Xunit;

namespace Kl.Cli.Tests;

public sealed class ProtocolContractTests
{
    private static JsonDocument Read(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void HandshakeRequestMatchesSharedContract()
    {
        using var document = Read("handshake_request.json");
        var root = document.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(Protocol.Version, root.GetProperty("kl_protocol").GetInt32());
        Assert.Equal(1, root.GetProperty("id").GetInt64());
        Assert.Equal(Protocol.Handshake, root.GetProperty("method").GetString());
        var parameters = root.GetProperty("params");
        Assert.Equal(Protocol.ClientName, parameters.GetProperty("client_name").GetString());
        var versions = parameters.GetProperty("protocol_versions");
        Assert.Equal(1, versions[0].GetInt32());
    }

    [Fact]
    public void HandshakeResponseMatchesSharedContract()
    {
        using var document = Read("handshake_response.json");
        Assert.Equal(Protocol.Version, document.RootElement.GetProperty("kl_protocol").GetInt32());
        var result = document.RootElement.GetProperty("result");
        Assert.Equal(Protocol.CoreName, result.GetProperty("server_name").GetString());
        Assert.Equal(Protocol.Version, result.GetProperty("protocol_version").GetInt32());
        Assert.True(result.GetProperty("min_protocol_version").GetInt32() <= Protocol.Version);
    }

    [Fact]
    public void PingMessagesMatchSharedContract()
    {
        using var request = Read("ping_request.json");
        Assert.Equal(Protocol.Ping, request.RootElement.GetProperty("method").GetString());
        Assert.Equal(
            "contract-test",
            request.RootElement.GetProperty("params").GetProperty("nonce").GetString());

        using var response = Read("ping_response.json");
        Assert.True(response.RootElement.GetProperty("result").GetProperty("pong").GetBoolean());
    }

    [Fact]
    public void EventNotificationMatchesSharedContract()
    {
        using var document = Read("event_turn_started.json");
        var root = document.RootElement;
        Assert.Equal(Protocol.Event, root.GetProperty("method").GetString());
        Assert.Equal(Protocol.Version, root.GetProperty("kl_protocol").GetInt32());
        var parameters = root.GetProperty("params");
        Assert.Equal(1, parameters.GetProperty("seq").GetInt64());
        Assert.Equal(
            "turn.started",
            parameters.GetProperty("event").GetProperty("type").GetString());
    }

    [Fact]
    public void VersionMismatchUsesStableErrorCode()
    {
        using var document = Read("error_version_mismatch.json");
        Assert.Equal(-32001, document.RootElement.GetProperty("error").GetProperty("code").GetInt64());
    }
}

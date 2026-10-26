using System.Text.Json;
using Xunit;

namespace Kl.Tui.Tests;

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
    public void UserMessageEventMatchesSharedContract()
    {
        using var document = Read("event_user_message.json");
        var root = document.RootElement;
        Assert.Equal(Protocol.Version, root.GetProperty("kl_protocol").GetInt32());
        Assert.Equal(Protocol.Event, root.GetProperty("method").GetString());
        var eventElement = root.GetProperty("params").GetProperty("event");
        Assert.Equal("user.message", eventElement.GetProperty("type").GetString());
        var data = eventElement.GetProperty("data");
        Assert.Equal("turn-1", data.GetProperty("turn_id").GetString());
        Assert.Equal("fix it", data.GetProperty("text").GetString());
    }

    [Fact]
    public void ProviderListResultMatchesSharedContract()
    {
        using var document = Read("provider_list_result.json");
        var result = document.RootElement.GetProperty("result");
        Assert.Equal("opencode", result.GetProperty("active_provider").GetString());
        Assert.Equal("gpt-5-responses", result.GetProperty("active_model").GetString());
        var models = result.GetProperty("models");
        Assert.Equal(3, models.GetArrayLength());
        Assert.Equal("openai-chat", models[0].GetProperty("protocol").GetString());
        Assert.True(models[0].GetProperty("enabled").GetBoolean());
        Assert.True(models[0].GetProperty("key_configured").GetBoolean());
        Assert.Equal("anthropic", models[1].GetProperty("protocol").GetString());
        Assert.Equal("openai-responses", models[2].GetProperty("protocol").GetString());
        Assert.Equal(
            "https://gateway.example/custom/responses",
            models[2].GetProperty("url").GetString());
    }

    [Fact]
    public void ReviewResultMatchesSharedContract()
    {
        using var document = Read("review_result.json");
        var result = document.RootElement.GetProperty("result");
        Assert.Equal("strict", result.GetProperty("mode").GetString());
        Assert.Equal(1, result.GetProperty("changed_files").GetInt32());
        var findings = result.GetProperty("findings");
        Assert.Equal(2, findings.GetArrayLength());
        Assert.Equal("secret-openai", findings[0].GetProperty("rule").GetString());
        Assert.Equal("src/a.rs", findings[0].GetProperty("file").GetString());
        Assert.Equal(3, findings[0].GetProperty("line").GetInt32());
        Assert.True(findings[1].GetProperty("low_confidence").GetBoolean());
        Assert.Equal(0, findings[1].GetProperty("line").GetInt32());
    }

    [Fact]
    public void VersionMismatchUsesStableErrorCode()
    {
        using var document = Read("error_version_mismatch.json");
        Assert.Equal(-32001, document.RootElement.GetProperty("error").GetProperty("code").GetInt64());
    }

    [Fact]
    public void SessionHistoryMatchesSharedContract()
    {
        using var request = Read("session_history_request.json");
        Assert.Equal(
            Protocol.SessionHistory,
            request.RootElement.GetProperty("method").GetString());
        var parameters = request.RootElement.GetProperty("params");
        Assert.Equal("session-1", parameters.GetProperty("session_id").GetString());
        Assert.Equal(20, parameters.GetProperty("limit").GetInt32());

        using var response = Read("session_history_result.json");
        var result = response.RootElement.GetProperty("result");
        Assert.Equal("session-1", result.GetProperty("session_id").GetString());
        var messages = result.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("fix it", messages[0].GetProperty("text").GetString());
        Assert.Equal(2, messages[0].GetProperty("seq").GetInt64());
        Assert.True(messages[0].TryGetProperty("timestamp", out _));
        var tool = messages[1].GetProperty("tool");
        Assert.Equal("shell", tool.GetProperty("name").GetString());
        Assert.True(tool.GetProperty("ok").GetBoolean());
        Assert.Equal("exit 0 in 5 ms", tool.GetProperty("summary").GetString());
        Assert.Equal("line1\nline2\n", tool.GetProperty("output").GetString());
        Assert.Equal("+new", tool.GetProperty("diff").GetString());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal("done", messages[2].GetProperty("text").GetString());
        Assert.Equal(
            "Check the files.",
            messages[2].GetProperty("reasoning").GetString());

        var raw = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "session_history_result.json"));
        foreach (var forbidden in new[] { "\"args\"", "\"api_key\"" })
        {
            Assert.DoesNotContain(forbidden, raw);
        }
    }
}

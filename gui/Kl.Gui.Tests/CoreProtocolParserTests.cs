using Kl.Client;
using Kl.Gui.Services;

namespace Kl.Gui.Tests;

/// <summary>
/// The GUI never reads core files; every session row and transcript entry is
/// mapped from protocol JSON by <see cref="CoreProtocolParser"/>.
/// </summary>
public sealed class CoreProtocolParserTests
{
    private static readonly DateTimeOffset Fallback = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void ParseSessionsReadsSessionListFields()
    {
        var result = TestData.Json(
            """
            {"sessions":[
                {"session_id":"s1","workspace":"D:\\ws","provider":"deepseek",
                 "model":"deepseek-chat","updated_at":"2026-11-03T10:00:00+08:00",
                 "message_count":7,"task":"修 bug","prompt":"原始提问","last_message":"最后一条"},
                {"session_id":"s2","workspace":"E:\\other","provider":"openai",
                 "model":"gpt","updated_at":"2026-11-02T09:00:00+08:00","message_count":0}
            ]}
            """);

        var sessions = CoreProtocolParser.ParseSessions(result);

        Assert.Equal(2, sessions.Count);
        var first = sessions[0];
        Assert.Equal("s1", first.SessionId);
        Assert.Equal(@"D:\ws", first.Workspace);
        Assert.Equal("deepseek", first.Provider);
        Assert.Equal("deepseek-chat", first.Model);
        Assert.Equal(7, first.MessageCount);
        Assert.Equal("修 bug", first.Task);
        Assert.Equal("原始提问", first.Prompt);
        Assert.Equal("最后一条", first.LastMessage);
        var second = sessions[1];
        Assert.Equal("s2", second.SessionId);
        Assert.Equal(0, second.MessageCount);
        Assert.Null(second.Task);
        Assert.Null(second.Prompt);
        Assert.Null(second.LastMessage);
    }

    [Fact]
    public void ParseSessionsReturnsEmptyWhenArrayIsMissing()
    {
        Assert.Empty(CoreProtocolParser.ParseSessions(TestData.Json("""{"sessions":null}""")));
        Assert.Empty(CoreProtocolParser.ParseSessions(TestData.Json("""{"other":1}""")));
    }

    [Fact]
    public void ParseHistoryReadsToolReasoningSeqAndTimestamp()
    {
        var result = TestData.Json(
            """
            {"messages":[
                {"role":"user","text":"请检查","timestamp":"2026-11-03T10:00:00+08:00","seq":3},
                {"role":"assistant","text":"答案","reasoning":"先思考","seq":4},
                {"role":"tool","text":"",
                 "tool":{"name":"shell","ok":false,"summary":"退出码 1","output":"error: boom","diff":"+ a\n- b"}}
            ]}
            """);

        var messages = CoreProtocolParser.ParseHistory(result);

        Assert.Equal(3, messages.Count);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("请检查", messages[0].Text);
        Assert.Equal(3, messages[0].Seq);
        Assert.Contains("2026-11-03", messages[0].Timestamp);
        Assert.Equal("先思考", messages[1].Reasoning);
        Assert.Equal(4, messages[1].Seq);
        var tool = messages[2];
        Assert.Equal("tool", tool.Role);
        Assert.Equal("shell", tool.ToolName);
        Assert.False(tool.ToolOk);
        Assert.Equal("退出码 1", tool.ToolSummary);
        Assert.Equal("error: boom", tool.ToolOutput);
        Assert.Contains("- b", tool.ToolDiff);
        Assert.Null(tool.Seq);
    }

    [Fact]
    public void ParseHistoryTreatsNonBooleanToolOkAsUnknown()
    {
        var result = TestData.Json(
            """{"messages":[{"role":"tool","text":"","tool":{"name":"shell","ok":"yes"}}]}""");

        var tool = Assert.Single(CoreProtocolParser.ParseHistory(result));

        Assert.Null(tool.ToolOk);
        Assert.Equal("shell", tool.ToolName);
        Assert.Null(tool.ToolOutput);
    }

    [Fact]
    public void ParseHistoryReturnsEmptyWhenMessagesAreMissing()
    {
        Assert.Empty(CoreProtocolParser.ParseHistory(TestData.Json("""{"messages":[]}""")));
        Assert.Empty(CoreProtocolParser.ParseHistory(TestData.Json("""{"other":1}""")));
    }

    [Fact]
    public void ParseCatalogMapsHandshakeProvidersModelsAndSessions()
    {
        var handshake = new HandshakeInfo("kl-core", "0.9.1", 1, 1);
        var providers = TestData.Json(
            """
            {"active_provider":"deepseek","active_model":"deepseek-chat",
             "models":[
                {"provider":"other","model":"other-model","key_configured":true},
                {"provider":"deepseek","model":"deepseek-chat","key_configured":true},
                {"provider":"deepseek","model":"deepseek-reasoner"}
             ]}
            """);
        var sessions = TestData.Json(
            """{"sessions":[{"session_id":"s1","workspace":"D:\\ws","updated_at":"2026-11-03T10:00:00+08:00","message_count":2}]}""");

        var snapshot = CoreProtocolParser.ParseCatalog(handshake, providers, sessions);

        Assert.True(snapshot.Success);
        Assert.Equal("0.9.1", snapshot.CoreVersion);
        Assert.Equal(1, snapshot.ProtocolVersion);
        Assert.Equal("deepseek", snapshot.Provider);
        Assert.Equal("deepseek-chat", snapshot.Model);
        Assert.Equal(3, snapshot.Models.Count);
        Assert.Contains("deepseek-reasoner", snapshot.Models);
        Assert.True(snapshot.KeyConfigured);
        Assert.Equal("s1", Assert.Single(snapshot.Sessions).SessionId);
    }

    [Fact]
    public void ParseCatalogWithoutProviderCatalogKeepsUnknownKeyAndEmptyModels()
    {
        var snapshot = CoreProtocolParser.ParseCatalog(
            new HandshakeInfo("kl-core", "0.9.1", 1, 1),
            TestData.Json("{}"),
            TestData.Json("""{"sessions":[]}"""));

        Assert.True(snapshot.Success);
        Assert.Empty(snapshot.Models);
        Assert.False(snapshot.KeyConfigured);
        Assert.Empty(snapshot.Sessions);
    }

    [Fact]
    public void DeriveTitlePrefersTaskThenPromptThenLastMessage()
    {
        Assert.Equal("任务", CoreProtocolParser.DeriveTitle(Summary("任务", "提问", "最后")));
        Assert.Equal("提问", CoreProtocolParser.DeriveTitle(Summary(null, "提问", "最后")));
        Assert.Equal("最后", CoreProtocolParser.DeriveTitle(Summary(null, null, "最后")));
        Assert.Equal("新会话", CoreProtocolParser.DeriveTitle(Summary(null, null, null)));
    }

    [Fact]
    public void DeriveTitleCollapsesWhitespaceAndTruncatesToFortyCharacters()
    {
        Assert.Equal("第一行 第二行", CoreProtocolParser.DeriveTitle(Summary("第一行\n  第二行", null, null)));

        var title = CoreProtocolParser.DeriveTitle(Summary(new string('x', 60), null, null));
        Assert.Equal(40, title.Length);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void ParseTimestampFallsBackWhenValueIsInvalid()
    {
        Assert.Equal(Fallback, CoreProtocolParser.ParseTimestamp(null, Fallback));
        Assert.Equal(Fallback, CoreProtocolParser.ParseTimestamp("not-a-date", Fallback));
        Assert.NotEqual(
            Fallback,
            CoreProtocolParser.ParseTimestamp("2026-11-03T10:00:00+08:00", Fallback));
    }

    private static CoreSessionSummary Summary(string? task, string? prompt, string? lastMessage) =>
        new("s", "w", "p", "m", "2026-11-03T10:00:00+08:00", 0, task, prompt, lastMessage);
}

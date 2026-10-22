namespace Kl.Tui;

public enum ChatCommandKind
{
    Init,
    Status,
    Permissions,
    Model,
    Review,
    Compact,
    Clear,
    Settings,
    Resume,
    New,
    Help,
    Exit,
    Agent,
    Skills,
    Mcp,
    Copy,
    Fork,
    Cost,
    Context,
    Diff,
    Export,
    Memory
}

public sealed record ChatCommandDefinition(
    string Name,
    ChatCommandKind Kind,
    string EnglishDescription,
    string ChineseDescription,
    IReadOnlyList<string> Aliases,
    bool Hidden = false);

public enum MemoryCommandAction
{
    List,
    Read,
    Write,
    Delete
}

public sealed record MemoryCommand(
    MemoryCommandAction Action,
    string Name,
    string Content,
    string? Scope);

public static class MemoryCommandParser
{
    /// <summary>
    /// Parses `/memory [list|read|write|delete] [name] [content]`. The name
    /// may be prefixed with `user:` to target the user memory store.
    /// </summary>
    public static bool TryParse(string? input, out MemoryCommand command)
    {
        command = new MemoryCommand(MemoryCommandAction.List, string.Empty, string.Empty, null);
        var text = input?.Trim();
        if (text is null)
        {
            return false;
        }

        var separator = text.IndexOfAny([' ', '\t']);
        var head = separator < 0 ? text : text[..separator];
        if (!head.Equals("/memory", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = separator < 0 ? string.Empty : text[(separator + 1)..].Trim();
        if (rest.Length == 0)
        {
            command = new MemoryCommand(MemoryCommandAction.List, string.Empty, string.Empty, null);
            return true;
        }

        var actionEnd = rest.IndexOfAny([' ', '\t']);
        var actionText = actionEnd < 0 ? rest : rest[..actionEnd];
        var arguments = actionEnd < 0 ? string.Empty : rest[(actionEnd + 1)..].Trim();
        var action = actionText.ToLowerInvariant() switch
        {
            "list" => MemoryCommandAction.List,
            "read" => MemoryCommandAction.Read,
            "write" => MemoryCommandAction.Write,
            "delete" => MemoryCommandAction.Delete,
            _ => (MemoryCommandAction?)null
        };
        if (action is null)
        {
            return false;
        }

        string? scope = null;
        var name = arguments;
        if (action == MemoryCommandAction.List)
        {
            name = string.Empty;
        }
        else
        {
            var nameEnd = arguments.IndexOfAny([' ', '\t']);
            name = nameEnd < 0 ? arguments : arguments[..nameEnd];
            arguments = nameEnd < 0 ? string.Empty : arguments[(nameEnd + 1)..].Trim();
        }

        if (name.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
        {
            scope = "user";
            name = name["user:".Length..];
        }

        command = new MemoryCommand(
            action.Value,
            name,
            action == MemoryCommandAction.Write ? arguments : string.Empty,
            scope);
        return true;
    }
}

public static class ChatCommandPalette
{
    public const int MaxVisible = 8;

    public static IReadOnlyList<string> AgentPresets { get; } =
        ["build", "plan", "explore", "review", "verify", "deep"];

    private static readonly IReadOnlyList<ChatCommandDefinition> All =
    [
        new(
            "/init",
            ChatCommandKind.Init,
            "create an AGENTS.md file with instructions for KL",
            "创建 AGENTS.md 并写入 KL 指令",
            ["/init"]),
        new(
            "/status",
            ChatCommandKind.Status,
            "show current session configuration",
            "查看当前会话配置",
            ["/status"]),
        new(
            "/permissions",
            ChatCommandKind.Permissions,
            "choose what KL is allowed to do",
            "选择 KL 的权限与审批方式",
            ["/permissions"]),
        new(
            "/model",
            ChatCommandKind.Model,
            "choose what model and reasoning effort to use",
            "选择模型和推理强度",
            ["/model"]),
        new(
            "/review",
            ChatCommandKind.Review,
            "review changes and find issues",
            "审查当前改动并发现问题",
            ["/review"]),
        new(
            "/compact",
            ChatCommandKind.Compact,
            "compact the current conversation",
            "压缩当前会话上下文",
            ["/compact"]),
        new(
            "/clear",
            ChatCommandKind.Clear,
            "clear the transcript and start a new chat",
            "清空记录并新建会话",
            ["/clear"]),
        new(
            "/settings",
            ChatCommandKind.Settings,
            "open settings",
            "打开设置",
            ["/settings"]),
        new(
            "/resume",
            ChatCommandKind.Resume,
            "resume a conversation",
            "恢复历史会话",
            ["/resume", "/history"]),
        new(
            "/new",
            ChatCommandKind.New,
            "start a new session",
            "新建会话",
            ["/new"]),
        new(
            "/help",
            ChatCommandKind.Help,
            "show keyboard shortcuts",
            "显示快捷键",
            ["/help"]),
        new(
            "/exit",
            ChatCommandKind.Exit,
            "exit KL",
            "退出 KL",
            ["/exit"]),
        new(
            "/agent",
            ChatCommandKind.Agent,
            "view or switch the agent preset",
            "查看或切换 Agent preset",
            ["/agent"]),
        new(
            "/skills",
            ChatCommandKind.Skills,
            "list available skills",
            "列出可用 Skills",
            ["/skills"]),
        new(
            "/mcp",
            ChatCommandKind.Mcp,
            "list MCP tools",
            "列出 MCP 工具",
            ["/mcp"]),
        new(
            "/copy",
            ChatCommandKind.Copy,
            "copy the last assistant message",
            "复制最后一条助手消息",
            ["/copy"],
            Hidden: true),
        new(
            "/fork",
            ChatCommandKind.Fork,
            "fork the current session",
            "分叉当前会话",
            ["/fork"]),
        new(
            "/cost",
            ChatCommandKind.Cost,
            "show token usage and estimated cost",
            "查看 token 用量和预估费用",
            ["/cost"]),
        new(
            "/context",
            ChatCommandKind.Context,
            "show context usage and compaction threshold",
            "查看上下文占用和压缩阈值",
            ["/context"]),
        new(
            "/diff",
            ChatCommandKind.Diff,
            "show workspace changes captured in this session",
            "查看本会话捕获的工作区改动",
            ["/diff"]),
        new(
            "/export",
            ChatCommandKind.Export,
            "export this conversation as markdown",
            "将当前会话导出为 Markdown",
            ["/export"]),
        new(
            "/memory",
            ChatCommandKind.Memory,
            "list, read, write or delete workspace memory",
            "查看、编辑或删除工作区记忆",
            ["/memory"])
    ];

    public static string Description(
        ChatCommandDefinition definition,
        UiLanguage language) =>
        language == UiLanguage.ChineseSimplified
            ? definition.ChineseDescription
            : definition.EnglishDescription;

    public static bool IsValidAgentPreset(string? preset) =>
        preset is not null && AgentPresets.Contains(preset);

    /// <summary>
    /// A palette query is a slash followed only by ASCII letters, digits or
    /// hyphens. Anything else (including spaces and Chinese) is prompt text.
    /// </summary>
    public static bool IsQuery(string? input)
    {
        if (input is null || !input.StartsWith('/'))
        {
            return false;
        }
        foreach (var character in input.AsSpan(1))
        {
            var allowed = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '-';
            if (!allowed)
            {
                return false;
            }
        }
        return true;
    }

    public static bool IsOpen(ChatTuiState state) =>
        state.Screen == ChatScreen.Chat
        && !state.ModelPicker.IsOpen
        && !state.CommandPaletteDismissed
        && IsQuery(state.Input)
        && Matches(state.Input).Count > 0;

    public static IReadOnlyList<ChatCommandDefinition> Matches(string? input)
    {
        if (!IsQuery(input))
        {
            return [];
        }

        var query = input ?? string.Empty;
        var visible = All.Where(command => !command.Hidden).ToList();
        var matches = new List<ChatCommandDefinition>(visible.Count);
        var exact = visible.FirstOrDefault(command => command.Aliases.Any(alias =>
            alias.Equals(query, StringComparison.OrdinalIgnoreCase)));
        if (exact is not null)
        {
            matches.Add(exact);
        }

        foreach (var command in visible)
        {
            if (ReferenceEquals(command, exact))
            {
                continue;
            }
            if (command.Aliases.Any(alias => alias.StartsWith(
                query,
                StringComparison.OrdinalIgnoreCase)))
            {
                matches.Add(command);
            }
        }
        return matches;
    }

    public static ChatCommandDefinition? Selected(ChatTuiState state)
    {
        if (!IsOpen(state))
        {
            return null;
        }

        var matches = Matches(state.Input);
        return matches[Math.Clamp(state.CommandPaletteSelection, 0, matches.Count - 1)];
    }

    public static void MoveSelection(ChatTuiState state, int delta)
    {
        if (!IsOpen(state))
        {
            return;
        }

        var count = Matches(state.Input).Count;
        if (count == 0)
        {
            state.CommandPaletteSelection = 0;
            return;
        }

        var next = (state.CommandPaletteSelection + delta) % count;
        if (next < 0)
        {
            next += count;
        }
        state.CommandPaletteSelection = next;
    }

    public static ChatCommandDefinition? FindExact(string? input)
    {
        var command = input?.Trim();
        if (string.IsNullOrEmpty(command))
        {
            return null;
        }
        return All.FirstOrDefault(definition => definition.Aliases.Any(alias =>
            alias.Equals(command, StringComparison.OrdinalIgnoreCase)));
    }
}

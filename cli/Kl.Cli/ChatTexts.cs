namespace Kl.Cli;

/// <summary>Localized chat surface copy. Command names stay English.</summary>
public static class ChatTexts
{
    public static string T(UiLanguage language, string english, string chinese) =>
        language == UiLanguage.ChineseSimplified ? chinese : english;

    public static string InputPlaceholder(UiLanguage language) =>
        T(language, "Ask KL to do anything", "描述一个任务，或输入 / 查看命令");

    public static string UserLabel(UiLanguage language) =>
        T(language, "You: ", "你：");

    public static string AssistantLabel(UiLanguage language) =>
        T(language, "KL: ", "KL：");

    public static string ToolLabel(UiLanguage language) =>
        T(language, "[tool] ", "[工具] ");

    public static string ErrorLabel(UiLanguage language) =>
        T(language, "[error] ", "[错误] ");

    public static string ApprovalTitle(UiLanguage language) =>
        T(language, "[approval]", "[审批]");

    public static string DiffTitle(UiLanguage language) =>
        T(language, "[diff]", "[差异]");

    public static string ThinkingLabel(UiLanguage language) =>
        T(language, "Thinking… ", "思考中… ");

    public static string ThinkingHidden(UiLanguage language) =>
        T(language, "Thinking… (Ctrl+R to show)", "思考中…（Ctrl+R 显示）");

    public static string ApprovalTool(UiLanguage language) =>
        T(language, "tool", "工具");

    public static string ApprovalCommand(UiLanguage language) =>
        T(language, "command/path", "命令/路径");

    public static string ApprovalRisk(UiLanguage language) =>
        T(language, "risk", "风险");

    public static string ApprovalAllow(UiLanguage language) =>
        T(language, "allow", "允许");

    public static string ApprovalDeny(UiLanguage language) =>
        T(language, "deny", "拒绝");

    public static string ApprovalDefaultRisk(UiLanguage language) =>
        T(language, "requires approval", "需要审批");

    public static string PaletteFooter(UiLanguage language) =>
        T(language, "↑/↓ select  Enter run  Esc close", "↑/↓ 选择  Enter 执行  Esc 关闭");

    public static string ModelPickerFooter(UiLanguage language) =>
        T(language, "↑/↓ select  Enter set default  Esc cancel", "↑/↓ 选择  Enter 设为默认  Esc 取消");

    public static string ApprovalFooter(UiLanguage language) =>
        T(language, "[a] allow  [d] deny", "[a] 允许  [d] 拒绝");

    public static string WorkingFooter(UiLanguage language) =>
        T(
            language,
            "Ctrl+C cancel  Shift+↑/↓ select  Ctrl+R reasoning",
            "Ctrl+C 取消  Shift+↑/↓ 选择  Ctrl+R 推理");

    public static string IdleFooter(UiLanguage language) =>
        T(
            language,
            "Ctrl+R reasoning  Ctrl+Shift+C copy  Ctrl+L clear  Ctrl+D exit  ? shortcuts",
            "Ctrl+R 推理  Ctrl+Shift+C 复制  Ctrl+L 清屏  Ctrl+D 退出  ? 快捷键");

    public static string NoProviderStatus(UiLanguage language) =>
        T(
            language,
            "No provider configured. Use /settings to configure one.",
            "未配置 Provider。使用 /settings 配置。");

    public static string NoProviderHomeHint(UiLanguage language) =>
        T(language, "/settings", "/settings");

    public static string UnknownCommand(UiLanguage language, string command) =>
        T(
            language,
            $"Unrecognized command '{command}'. Type \"/\" for a list of supported commands.",
            $"未知命令 '{command}'。输入 \"/\" 查看支持的命令。");

    public static string Copied(UiLanguage language, int lines) =>
        T(language, $"Copied {lines} lines", $"已复制 {lines} 行");

    public static string CopyFailed(UiLanguage language, string error) =>
        T(language, "copy failed: " + error, "复制失败：" + error);

    public static string NoAssistantToCopy(UiLanguage language) =>
        T(language, "no assistant message to copy", "没有可复制的助手消息");

    public static string NoSelectionToCopy(UiLanguage language) =>
        T(language, "no text selected", "没有选中的文本");

    public static string NoModels(UiLanguage language) =>
        T(
            language,
            "No enabled models found. Use /settings to configure a provider.",
            "没有可用的模型。使用 /settings 配置 Provider。");

    public static string ModelDefaultSet(UiLanguage language, string provider, string model) =>
        T(language, $"default model: {provider}/{model}", $"默认模型：{provider}/{model}");

    public static string SessionStatusTitle(UiLanguage language) =>
        T(language, "Session", "会话");

    public static string ProviderLabel(UiLanguage language) =>
        T(language, "Provider", "提供方");

    public static string ModelLabel(UiLanguage language) =>
        T(language, "Model", "模型");

    public static string SessionLabel(UiLanguage language) =>
        T(language, "Session", "会话");

    public static string WorkspaceLabel(UiLanguage language) =>
        T(language, "Workspace", "工作区");

    public static string MessagesLabel(UiLanguage language) =>
        T(language, "messages", "消息");

    public static string ReviewFindings(UiLanguage language, int count) =>
        T(language, $"Review: {count} finding(s)", $"审查：{count} 个发现");

    public static string ReviewFindingLine(
        UiLanguage language,
        string severity,
        string location,
        string rule) =>
        T(language, $"[{severity}] {location} {rule}", $"[{severity}] {location} {rule}");

    public static string ReviewMore(UiLanguage language, int remaining) =>
        T(language, $"… {remaining} more finding(s)", $"… 还有 {remaining} 个发现");

    public static string CompactResult(UiLanguage language, int omitted, long? tokens) =>
        tokens is null
            ? T(language, $"Compacted {omitted} message(s)", $"已压缩 {omitted} 条消息")
            : T(
                language,
                $"Compacted {omitted} message(s) · estimated {tokens} tokens",
                $"已压缩 {omitted} 条消息 · 估算 {tokens} tokens");

    public static string InitCreated(UiLanguage language, string path) =>
        T(language, $"AGENTS.md ready at {path}", $"AGENTS.md 已就绪：{path}");

    public static string InitUnavailable(UiLanguage language, string error) =>
        T(language, $"workspace.init unavailable: {error}", $"workspace.init 不可用：{error}");

    public static string CompactUnavailable(UiLanguage language, string error) =>
        T(language, $"session.compact unavailable: {error}", $"session.compact 不可用：{error}");

    public static string StatusFallback(UiLanguage language, string error) =>
        T(language, $"session.status unavailable ({error}); local values shown", $"session.status 不可用（{error}），显示本地值");

    public static string PermissionsHint(UiLanguage language) =>
        T(
            language,
            "Agent permissions are not implemented yet; showing the Agent settings group.",
            "Agent 权限设置尚未实现；已打开 Agent 分组。");

    public static string Working(UiLanguage language) =>
        T(language, "working", "处理中");

    public static string Cancelling(UiLanguage language) =>
        T(language, "cancelling", "正在取消");

    public static string NoMatchingCommand(UiLanguage language) =>
        T(language, "no matching command", "没有匹配的命令");

    public static string NewSessionUnavailable(UiLanguage language, string error) =>
        T(language, $"new session unavailable: {error}", $"无法新建会话：{error}");

    public static string NewSession(UiLanguage language, string sessionId) =>
        T(language, $"New session {sessionId}", $"新会话 {sessionId}");

    public static string ResumedSession(UiLanguage language, string sessionId, int messages) =>
        T(
            language,
            $"Resumed session {sessionId} ({messages} messages)",
            $"已恢复会话 {sessionId}（{messages} 条消息）");

    public static string HistoryUnavailable(UiLanguage language, string error) =>
        T(language, $"history unavailable: {error}", $"历史不可用：{error}");

    public static string OmittedLines(UiLanguage language, int count) =>
        T(language, $"… ({count} lines omitted)", $"…（省略 {count} 行）");

    public static string WorkedFor(UiLanguage language, double seconds, string time) =>
        T(language, $"Worked for {seconds:0.0}s • {time}", $"耗时 {seconds:0.0}s • {time}");

    public static string HistoryTitle(UiLanguage language) =>
        T(language, "History", "历史");

    public static string Workspace(UiLanguage language, string workspace) =>
        T(language, $"Workspace: {workspace}", $"工作区：{workspace}");

    public static string Selected(UiLanguage language) =>
        T(language, "Selected", "已选择");

    public static string Messages(UiLanguage language, int count) =>
        T(language, $"messages: {count}", $"消息：{count}");

    public static string Last(UiLanguage language, string value) =>
        T(language, $"last: {value}", $"最后：{value}");

    public static string Prompt(UiLanguage language, string value) =>
        T(language, $"prompt: {value}", $"提示：{value}");

    public static string NoSessions(UiLanguage language) =>
        T(
            language,
            "No sessions yet. Press n to start a new session.",
            "暂无会话。按 n 新建会话。");

    public static string ShortcutsTitle(UiLanguage language) =>
        T(language, "Shortcuts", "快捷键");

    public static IReadOnlyList<string> Shortcuts(UiLanguage language) =>
        language == UiLanguage.ChineseSimplified
            ? new[]
            {
                "Enter            发送消息",
                "Ctrl+C           取消当前 turn；有选择时复制；空闲时退出",
                "Shift+↑/↓        扩展聊天记录选择",
                "Ctrl+Shift+C     复制最后一条助手消息",
                "/copy            复制最后一条助手消息",
                "Ctrl+L           清屏并重绘",
                "Ctrl+D           退出",
                "Ctrl+R           显示/隐藏 reasoning",
                "↑ / ↓            滚动聊天记录",
                "?                显示或关闭此页",
                "Esc              返回"
            }
            : new[]
            {
                "Enter            send message",
                "Ctrl+C           cancel turn, copy selection, or exit when idle",
                "Shift+↑/↓        extend transcript selection",
                "Ctrl+Shift+C     copy the last assistant message",
                "/copy            copy the last assistant message",
                "Ctrl+L           clear the screen and redraw",
                "Ctrl+D           exit",
                "Ctrl+R           show or hide reasoning",
                "↑ / ↓            scroll transcript",
                "?                show or close this shortcut page",
                "Esc              back"
            };

    public static IReadOnlyList<string> Home(UiLanguage language, bool hasProvider)
    {
        var lines = new List<string>();
        if (language == UiLanguage.ChineseSimplified)
        {
            lines.Add("开始使用：描述一个任务，或尝试以下命令：");
            lines.Add(string.Empty);
            lines.Add("  /init          创建 AGENTS.md 并写入 KL 指令");
            lines.Add("  /status        查看当前会话配置");
            lines.Add("  /permissions   选择 KL 的权限与审批方式");
            lines.Add("  /model         选择模型和推理强度");
            lines.Add("  /review        审查当前改动并发现问题");
        }
        else
        {
            lines.Add("To get started, describe a task or try one of these commands:");
            lines.Add(string.Empty);
            lines.Add("  /init          create an AGENTS.md file with instructions for KL");
            lines.Add("  /status        show current session configuration");
            lines.Add("  /permissions   choose what KL is allowed to do");
            lines.Add("  /model         choose what model and reasoning effort to use");
            lines.Add("  /review        review changes and find issues");
        }

        if (!hasProvider)
        {
            lines.Add(string.Empty);
            lines.Add(language == UiLanguage.ChineseSimplified
                ? "  /settings      配置 Provider 和模型"
                : "  /settings      configure a provider and model");
        }
        return lines;
    }
}

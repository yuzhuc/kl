namespace Kl.Cli;

/// <summary>Localized chat surface copy. Command names stay English.</summary>
public static class ChatTexts
{
    public static string T(UiLanguage language, string english, string chinese) =>
        language == UiLanguage.ChineseSimplified ? chinese : english;

    public static string InputPlaceholder(UiLanguage language) =>
        T(language, KlBrand.InputPlaceholder, "让 KL 做点什么，或输入 / 查看命令");

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

    public static string StartupStage(UiLanguage language, string stage) =>
        stage switch
        {
            "starting-core" => T(language, "Starting KL core", "正在启动 KL core"),
            "handshaking" => T(language, "Connecting to core", "正在连接 core"),
            "opening-session" => T(language, "Opening session", "正在打开会话"),
            "session-ready" => T(language, "Session ready", "会话已就绪"),
            "loading-transcript" => T(language, "Loading conversation", "正在加载对话"),
            "loading-history" => T(language, "Loading history", "正在加载历史"),
            "checking-environment" => T(language, "Checking environment", "正在检查环境"),
            "ready" => T(language, "Ready", "就绪"),
            _ => stage
        };

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

    public static string WarningNoticeLabel(UiLanguage language, int count) =>
        T(
            language,
            $"⚠ {count} warning{(count == 1 ? string.Empty : "s")}",
            $"⚠ {count} 个警告");
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

    public static string HistoryGroupBy(UiLanguage language, HistoryGrouping grouping) =>
        T(
            language,
            $"Group: {HistoryGroupingLabel(language, grouping)}",
            $"分组：{HistoryGroupingLabel(language, grouping)}");

    public static string HistoryGroupingLabel(UiLanguage language, HistoryGrouping grouping) =>
        grouping switch
        {
            HistoryGrouping.Status => T(language, "Status", "状态"),
            HistoryGrouping.Model => T(language, "Model", "模型"),
            _ => T(language, "Workspace", "工作区")
        };

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

    public static string HistoryFilterLabel(UiLanguage language, HistoryFilter filter) =>
        filter switch
        {
            HistoryFilter.NeedsYou => T(language, "Needs you", "待处理"),
            HistoryFilter.Working => T(language, "Working", "运行中"),
            HistoryFilter.Ready => T(language, "Ready", "就绪"),
            HistoryFilter.Inactive => T(language, "Inactive", "已结束"),
            _ => T(language, "All", "全部")
        };

    public static string HistoryStatus(UiLanguage language, HistoryTaskStatus status) =>
        status switch
        {
            HistoryTaskStatus.NeedsYou => T(language, "Needs you", "待处理"),
            HistoryTaskStatus.Working => T(language, "Working", "运行中"),
            HistoryTaskStatus.Ready => T(language, "Ready", "就绪"),
            _ => T(language, "Inactive", "已结束")
        };

    public static string HistorySearch(UiLanguage language) =>
        T(language, "Search", "搜索");

    public static string TaskDetails(UiLanguage language) =>
        T(language, "Task details", "任务详情");

    public static string NoTaskSelected(UiLanguage language) =>
        T(language, "No task selected.", "未选择任务。");

    public static string TasksColumn(UiLanguage language) =>
        T(language, "Tasks", "任务");

    public static string StatusColumn(UiLanguage language) =>
        T(language, "Status", "状态");

    public static string UpdatedColumn(UiLanguage language) =>
        T(language, "Updated", "更新");

    public static string ProjectLabel(UiLanguage language) =>
        T(language, "Project", "项目");

    public static string PromptLabel(UiLanguage language) =>
        T(language, "Prompt", "提示");

    public static string LastMessageLabel(UiLanguage language) =>
        T(language, "Last message", "最后消息");

    public static string ReasoningLabel(UiLanguage language) =>
        T(language, "Reasoning", "模型等级");

    public static string UntitledTask(UiLanguage language) =>
        T(language, "Untitled task", "未命名任务");

    public static string NoSessions(UiLanguage language) =>
        T(
            language,
            "No sessions yet. Press n to start a new session.",
            "暂无会话。按 n 新建会话。");

    public static string NoSearchResults(UiLanguage language) =>
        T(
            language,
            "No tasks match this search.",
            "没有匹配的任务。");

    public static IReadOnlyList<(string Key, string Label)> HistoryFooterHints(
        UiLanguage language,
        bool searching) =>
        searching
            ? language == UiLanguage.ChineseSimplified
                ? [("↑/↓", "选择"), ("Enter", "保留"), ("Esc", "清空")]
                : [("↑/↓", "select"), ("Enter", "keep"), ("Esc", "clear")]
            : language == UiLanguage.ChineseSimplified
                ? [
                    ("r", "重命名"), ("↑/↓", "选择"), ("Enter", "恢复"), ("n", "新建"),
                    ("delete", "删除"), ("/", "搜索")
                ]
                : [
                    ("r", "rename"), ("↑/↓", "select"), ("Enter", "resume"), ("n", "new"),
                    ("delete", "delete"), ("/", "search")
                ];

    public static string HistoryDeleteConfirm(UiLanguage language) =>
        T(language, "again to delete", "再次确认删除");

    public static string HistoryDeleteCancel(UiLanguage language) =>
        T(language, "cancel", "取消");

    public static string CannotDeleteCurrentSession(UiLanguage language) =>
        T(
            language,
            "Cannot delete the active session.",
            "不能删除当前正在使用的会话。");

    public static string HistoryDeleted(UiLanguage language) =>
        T(language, "Task deleted", "任务已删除");

    public static string HistoryDeleteFailed(UiLanguage language, string error) =>
        T(language, $"delete failed: {error}", $"删除失败：{error}");

    public static string HistoryRename(UiLanguage language) =>
        T(language, "Rename", "重命名");

    public static string HistoryRenameEmpty(UiLanguage language) =>
        T(language, "Task name cannot be empty", "任务名称不能为空");

    public static string HistoryRenamed(UiLanguage language) =>
        T(language, "Task renamed", "任务已重命名");

    public static string HistoryRenameFailed(UiLanguage language, string error) =>
        T(language, $"rename failed: {error}", $"重命名失败：{error}");

    public static string NoRunningTask(UiLanguage language) =>
        T(language, "No running task to stop", "没有可停止的运行中任务");

    public static string HistoryShortcutsTitle(UiLanguage language) =>
        T(language, "History shortcuts", "历史页快捷键");

    public static IReadOnlyList<(string Key, string Label)> HistoryNavigateShortcuts(
        UiLanguage language) =>
        language == UiLanguage.ChineseSimplified
            ? [
                ("↑/↓, j/k", "选择任务"), ("PageUp/PageDown", "翻页"),
                ("Home/End", "第一项/最后一项"), ("Enter, o", "恢复任务"),
                ("Ctrl+C", "退出")
            ]
            : [
                ("↑/↓, j/k", "select task"), ("PageUp/PageDown", "page"),
                ("Home/End", "first/last"), ("Enter, o", "resume task"),
                ("Ctrl+C", "quit")
            ];

    public static IReadOnlyList<(string Key, string Label)> HistoryTaskShortcuts(
        UiLanguage language) =>
        language == UiLanguage.ChineseSimplified
            ? [
                ("n", "新建任务"), ("r", "重命名任务"),
                ("x", "停止任务"), ("Backspace/Delete", "删除任务"),
                ("Tab/Shift+Tab", "切换筛选")
            ]
            : [
                ("n", "new task"), ("r", "rename task"),
                ("x", "stop running task"), ("Backspace/Delete", "delete task"),
                ("Tab/Shift+Tab", "cycle filters")
            ];

    public static IReadOnlyList<(string Key, string Label)> HistoryViewShortcuts(
        UiLanguage language) =>
        language == UiLanguage.ChineseSimplified
            ? [
                ("/, F3", "搜索"), ("g", "切换分组"),
                ("?", "快捷键"), ("Esc", "返回")
            ]
            : [
                ("/, F3", "search"), ("g", "cycle grouping"),
                ("?", "shortcuts"), ("Esc", "back")
            ];

    public static string SecondsAgo(UiLanguage language, int value) =>
        T(language, $"{value}s ago", $"{value}秒前");

    public static string MinutesAgo(UiLanguage language, int value) =>
        T(language, $"{value}m ago", $"{value}分前");

    public static string HoursAgo(UiLanguage language, int value) =>
        T(language, $"{value}h ago", $"{value}小时前");

    public static string DaysAgo(UiLanguage language, int value) =>
        T(language, $"{value}d ago", $"{value}天前");

    public static string ShortcutsTitle(UiLanguage language) =>
        T(language, "Keyboard shortcuts", "键盘快捷键");

    public static IReadOnlyList<(string Title, IReadOnlyList<(string Key, string Label)> Entries)>
        ChatShortcutGroups(UiLanguage language) =>
        language == UiLanguage.ChineseSimplified
            ? [
                ("编辑", [
                    ("/", "命令"), ("@", "提及文件"), ("Enter", "发送消息"),
                    ("Ctrl+Shift+C", "复制最后消息"), ("/copy", "复制最后消息")
                ]),
                ("会话", [
                    ("Ctrl+C", "取消 / 复制 / 退出"), ("Ctrl+R", "显示或隐藏推理"),
                    ("Ctrl+L", "清屏"), ("Ctrl+D", "退出")
                ]),
                ("聊天记录（先打开）", [
                    ("↑/↓", "滚动"), ("Shift+↑/↓", "选择"), ("Ctrl+C", "复制选择"),
                    ("Esc", "返回")
                ])
            ]
            : [
                ("Compose", [
                    ("/", "Commands"), ("@", "Mention files"), ("Enter", "Send message"),
                    ("Ctrl+Shift+C", "Copy last message"), ("/copy", "Copy last message")
                ]),
                ("Session", [
                    ("Ctrl+C", "Cancel / copy / exit"), ("Ctrl+R", "Show or hide reasoning"),
                    ("Ctrl+L", "Clear screen"), ("Ctrl+D", "Exit")
                ]),
                ("Transcript (open first)", [
                    ("↑/↓", "Scroll"), ("Shift+↑/↓", "Select"), ("Ctrl+C", "Copy selection"),
                    ("Esc", "Back")
                ])
            ];

    public static IReadOnlyList<string> Home(UiLanguage language)
    {
        if (language == UiLanguage.ChineseSimplified)
        {
            return
            [
                "开始使用：描述一个任务，或尝试以下命令：",
                string.Empty,
                "/init - 创建 AGENTS.md 并写入 KL 指令",
                "/status - 查看当前会话配置",
                "/permissions - 选择 KL 的权限与审批方式",
                "/model - 选择模型和推理强度",
                "/review - 审查当前改动并发现问题"
            ];
        }

        return
        [
            "To get started, describe a task or try one of these commands:",
            string.Empty,
            "/init - create an AGENTS.md file with instructions for KL",
            "/status - show current session configuration",
            "/permissions - choose what KL is allowed to do",
            "/model - choose what model and reasoning effort to use",
            "/review - review changes and find issues"
        ];
    }

    public static string MentionTitle(UiLanguage language) =>
        T(language, "Mentions", "提及");

    public static string MentionDescription(UiLanguage language) =>
        T(language, "Files and directories", "文件与目录");

    public static string MentionSearchPrompt(UiLanguage language) =>
        T(language, "Type to search mentions", "输入以搜索提及");


    public static string MentionFilterLabel(UiLanguage language, MentionFilter filter) =>
        filter == MentionFilter.FilesystemOnly
            ? T(language, "Filesystem Only", "仅文件系统")
            : T(language, "All Results", "全部结果");

    public static string MentionFileType(UiLanguage language) =>
        T(language, "File", "文件");

    public static string NoFilesFound(UiLanguage language) =>
        T(language, "No files found", "未找到文件");

}

using System.Text.Json;
using Xunit;

namespace Kl.Cli.Tests;

public sealed class ChatTuiTests
{
    [Fact]
    public void HomeShowsCodexStyleEmptyState()
    {
        var state = new ChatTuiState();
        var render = ChatTuiRenderer.Render(100, 24, state);
        var plain = DiffRender.Ansi.Strip(render);

        Assert.Contains(
            plain.Split('\n'),
            line => line.Trim() == KlBrand.TitleLine);
        Assert.Contains("/init - create an AGENTS.md file", plain);
        Assert.Contains("/model - choose what model and reasoning effort to use", plain);
        Assert.Contains("› Ask KL to do anything", plain);
        Assert.Contains("? for shortcuts", plain);
        Assert.DoesNotContain("██╗  ██╗██╗", plain);
    }

    [Fact]
    public void HomeChineseUsesLocalizedPromptAndShortcutHint()
    {
        var state = new ChatTuiState();
        state.Settings.SetLanguage(UiLanguage.ChineseSimplified);
        var render = ChatTuiRenderer.Render(100, 24, state);
        var plain = DiffRender.Ansi.Strip(render);

        Assert.Contains("让 KL 做点什么", plain);
        Assert.Contains("? 快捷键", plain);
    }

    [Fact]
    public void ChatEnglishSnapshot()
    {
        var state = new ChatTuiState("D:\\kl");
        var lines = new List<string>
        {
            string.Empty,
            "  >_ KL (v0.1.0)",
            "     D:\\kl",
            string.Empty,
            "  To get started, describe a task or try one of these commands:",
            string.Empty,
            "  /init - create an AGENTS.md file with instructions for KL",
            "  /status - show current session configuration",
            "  /permissions - choose what KL is allowed to do",
            "  /model - choose what model and reasoning effort to use",
            "  /review - review changes and find issues"
        };
        lines.AddRange(Enumerable.Repeat(string.Empty, 10));
        lines.Add("  › Ask KL to do anything");
        lines.Add("  no provider configured · D:\\kl");
        lines.Add("  ← for agents · ? for shortcuts");

        Assert.Equal(string.Join('\n', lines), Snapshot(ChatTuiRenderer.Render(80, 24, state)));
    }

    [Fact]
    public void ChatChineseSnapshot()
    {
        var state = new ChatTuiState("D:\\kl");
        state.Settings.SetLanguage(UiLanguage.ChineseSimplified);
        var lines = new List<string>
        {
            string.Empty,
            "  >_ KL (v0.1.0)",
            "     D:\\kl",
            string.Empty,
            "  开始使用：描述一个任务，或尝试以下命令：",
            string.Empty,
            "  /init - 创建 AGENTS.md 并写入 KL 指令",
            "  /status - 查看当前会话配置",
            "  /permissions - 选择 KL 的权限与审批方式",
            "  /model - 选择模型和推理强度",
            "  /review - 审查当前改动并发现问题"
        };
        lines.AddRange(Enumerable.Repeat(string.Empty, 10));
        lines.Add("  › 让 KL 做点什么，或输入 / 查看命令");
        lines.Add("  未配置 Provider · D:\\kl");
        lines.Add("  ← 代理 · ? 快捷键");

        Assert.Equal(string.Join('\n', lines), Snapshot(ChatTuiRenderer.Render(80, 24, state)));
    }

    [Fact]
    public void HomeUsesCodexTitleCommandAndPathColors()
    {
        var state = new ChatTuiState("D:\\kl", "openai", "gpt-5", "s1")
        {
            ReasoningEffort = "high"
        };

        var render = ChatTuiRenderer.Render(80, 24, state);

        Assert.Contains(
            DiffRender.Ansi.BoldAccent + ">_" + DiffRender.Ansi.Reset +
            DiffRender.Ansi.Bold + " KL" + DiffRender.Ansi.Reset +
            DiffRender.Ansi.Secondary + " (v0.1.0)" + DiffRender.Ansi.Reset,
            render);
        Assert.Contains(
            "     " + DiffRender.Ansi.Path + "D:\\kl" + DiffRender.Ansi.Reset,
            render);
        Assert.Contains(
            DiffRender.Ansi.Bold + "/init" + DiffRender.Ansi.Reset +
            DiffRender.Ansi.Secondary +
            " - create an AGENTS.md file with instructions for KL" +
            DiffRender.Ansi.Reset,
            render);
        Assert.Contains(
            "  " + DiffRender.Ansi.BoldAccent + "openai/gpt-5" +
            DiffRender.Ansi.Reset + " " + DiffRender.Ansi.Bold + "high" +
            DiffRender.Ansi.Reset + DiffRender.Ansi.Secondary + " · " +
            DiffRender.Ansi.Reset + DiffRender.Ansi.Path + "D:\\kl" +
            DiffRender.Ansi.Reset,
            render);
    }

    [Fact]
    public void HomeAndModelLineAbbreviateUserDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var state = new ChatTuiState(home, "openai", "gpt-5", "s1")
        {
            ReasoningEffort = "high"
        };

        var plain = DiffRender.Ansi.Strip(ChatTuiRenderer.Render(80, 24, state));

        Assert.Contains("     ~", plain);
        Assert.Contains("openai/gpt-5 high · ~", plain);
    }

    [Fact]
    public void DefaultReasoningEffortIsHidden()
    {
        var state = new ChatTuiState("D:\\kl", "openai", "gpt-5", "s1")
        {
            ReasoningEffort = "default"
        };

        var plain = DiffRender.Ansi.Strip(ChatTuiRenderer.Render(80, 24, state));

        Assert.Contains("openai/gpt-5 · D:\\kl", plain);
        Assert.DoesNotContain("openai/gpt-5 default", plain);
    }
    [Fact]
    public void ShortcutsPageUsesCodexGroupsAndFooter()
    {
        var state = new ChatTuiState("D:\\kl", "openai", "gpt-5", "s1")
        {
            Screen = ChatScreen.Shortcuts,
            ShortcutReturnScreen = ChatScreen.Chat,
            ReasoningEffort = "high"
        };

        var plain = DiffRender.Ansi.Strip(ChatTuiRenderer.Render(110, 30, state));

        Assert.Contains("/init - create an AGENTS.md file with instructions for KL", plain);
        Assert.Contains("/review - review changes and find issues", plain);
        Assert.True(
            plain.IndexOf("/review - review changes", StringComparison.Ordinal) <
            plain.IndexOf("Keyboard shortcuts", StringComparison.Ordinal));
        Assert.Contains("Keyboard shortcuts", plain);
        Assert.Contains("Compose", plain);
        Assert.Contains("Session", plain);
        Assert.Contains("Transcript (open first)", plain);
        Assert.Contains("/settings customize", plain);
        Assert.Contains("openai/gpt-5 high · D:\\kl", plain);
        Assert.EndsWith("? / esc close", plain.Split('\n')[^1].TrimEnd());
    }
    [Fact]
    public void ComposerUsesCodexPromptAndPlaceholderColors()
    {
        var state = new ChatTuiState();

        var render = ChatTuiRenderer.Render(80, 24, state);

        Assert.Contains(
            DiffRender.Ansi.ComposerBackground + "  " + DiffRender.Ansi.Bold + "›" +
            DiffRender.Ansi.Reset + DiffRender.Ansi.ComposerBackground + " " +
            DiffRender.Ansi.Secondary + KlBrand.InputPlaceholder +
            DiffRender.Ansi.ComposerBackground,
            render);
    }
    [Fact]
    public void UserMessageUsesBlockFillAndSecondaryTextIsNotDim()
    {
        var state = new ChatTuiState();
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.User, "fix it"));

        var render = ChatTuiRenderer.Render(80, 24, state);

        Assert.Contains(DiffRender.Ansi.UserMessage, render);
        Assert.Contains(DiffRender.Ansi.BoldAccent, render);
        Assert.Contains(DiffRender.Ansi.Secondary, render);
        Assert.DoesNotContain("\u001b[2m", render);
    }

    [Fact]
    public void MouseSelectionRendersBlueAndCopiesPlainText()
    {
        var state = new ChatTuiState();
        state.AddInfo("one");
        state.BeginMouseSelection(0, 0);
        state.ExtendMouseSelection(0, 2);
        state.EndMouseSelection(0, 2);

        var render = ChatTuiRenderer.Render(80, 24, state);

        Assert.Contains(DiffRender.Ansi.Selection, render);
        Assert.Equal("one", state.SelectedText(80));
    }

    [Fact]
    public void RenderShowsHeaderInputAndFooter()
    {
        var state = new ChatTuiState("D:\\work", "openai", "gpt-5", "session-1");
        var render = ChatTuiRenderer.Render(100, 24, state);
        var lines = render.Split('\n');

        Assert.Equal(24, lines.Length);
        Assert.Equal(">_ KL (v0.1.0)", DiffRender.Ansi.Strip(lines[1]).Trim());
        Assert.Contains("› Ask KL to do anything", DiffRender.Ansi.Strip(render));
        Assert.Contains("? for shortcuts", DiffRender.Ansi.Strip(render.Split('\n')[^1]));
    }

    [Fact]
    public void ChatFooterReturnsAfterTranscriptStarts()
    {
        var state = new ChatTuiState("D:\\work", "openai", "gpt-5", "session-1");
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.Info, "session started"));

        var plain = DiffRender.Ansi.Strip(ChatTuiRenderer.Render(100, 24, state));
        var footer = plain.Split('\n')[^1];
        Assert.Contains("openai/gpt-5", plain);
        Assert.Contains("Ctrl+R reasoning", footer);
    }

    [Fact]
    public void HomeShowsModelEffortAndRightAlignedWarning()
    {
        var state = new ChatTuiState("D:\\work", "deepseek", "deepseek-flash", "session-1")
        {
            ReasoningEffort = "high",
            WarningCount = 5
        };

        var plain = DiffRender.Ansi.Strip(ChatTuiRenderer.Render(100, 24, state));
        var lines = plain.Split('\n');
        Assert.Contains("deepseek/deepseek-flash high · D:\\work", plain);
        Assert.StartsWith("← for agents · ? for shortcuts", lines[^1].TrimStart());
        Assert.EndsWith("⚠ 5 warnings · f2 to view", lines[^1].TrimEnd());
    }

    [Fact]
    public void RenderShowsTranscriptEntryKinds()
    {
        var state = new ChatTuiState();
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.User, "fix it"));
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.Assistant, "done"));
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.Tool, "shell cargo test"));
        state.Apply(new SessionEvent(
            SessionEventKind.ApprovalRequested,
            Name: "edit",
            ApprovalId: "a1",
            Action: "write",
            Resource: "src/lib.rs",
            Reason: "modifies a tracked file"));
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.Diff, "-old\n+new"));
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.Error, "boom"));

        var render = ChatTuiRenderer.Render(100, 24, state);

        Assert.Contains("You: fix it", render);
        Assert.Contains("KL: done", render);
        Assert.Contains("[tool] shell cargo test", render);
        Assert.Contains("[approval]", render);
        Assert.Contains("tool: edit", render);
        Assert.Contains("command/path: write src/lib.rs", render);
        Assert.Contains("+new", render);
        Assert.Contains("[error] boom", render);
    }

    [Fact]
    public void RenderShowsWorkedDurationAfterTurn()
    {
        var state = new ChatTuiState();
        state.Apply(new SessionEvent(SessionEventKind.TurnStarted));
        state.TurnStartedAt = DateTimeOffset.Now.AddSeconds(-1.2);
        state.Apply(new SessionEvent(
            SessionEventKind.TurnCompleted,
            Status: "completed",
            Text: "done"));

        var render = ChatTuiRenderer.Render(100, 24, state);

        Assert.Contains("KL: done", render);
        Assert.Contains("Worked for 1.2s •", render);
    }

    [Fact]
    public void TranscriptScrollShowsOlderEntries()
    {
        var state = new ChatTuiState();
        for (var index = 0; index < 40; index++)
        {
            state.Transcript.Add(new TranscriptEntry(
                TranscriptKind.Info,
                $"line-{index:00}"));
        }
        state.TranscriptScroll = 100;

        var render = ChatTuiRenderer.Render(80, 12, state);

        Assert.Contains("line-00", render);
        Assert.DoesNotContain("line-39", render);
    }

    [Fact]
    public void HistoryShowsSelectionAndSelectedDetails()
    {
        var state = new ChatTuiState("D:\\b", "p2", "m2", "s2")
        {
            Screen = ChatScreen.History,
            HistorySelection = 1,
            History =
            [
                new HistorySession(
                    "s1",
                    "D:\\a",
                    "p1",
                    "m1",
                    "2026-10-17T10:00:00Z",
                    2,
                    "completed",
                    "task one",
                    "prompt one",
                    "last one"),
                new HistorySession(
                    "s2",
                    "D:\\b",
                    "p2",
                    "m2",
                    "2026-10-17T11:00:00Z",
                    4,
                    "ready",
                    "task two",
                    "prompt two",
                    "last two")
            ]
        };

        var render = ChatTuiRenderer.Render(100, 24, state);
        var plain = DiffRender.Ansi.Strip(render);

        Assert.Contains("D:\\a  1", plain);
        Assert.Contains("› ○ task two", plain);
        Assert.Contains("Task details", plain);
        Assert.Contains("○ Ready", plain);
        Assert.Contains("Model: p2/m2", plain);
        Assert.Contains("Project", plain);
        Assert.Contains("D:\\b", plain);
        Assert.Contains("Prompt", plain);
        Assert.Contains("prompt two", plain);
        Assert.Contains("Last message", plain);
        Assert.Contains("last two", plain);
        Assert.Contains(
            "↑/↓ select",
            DiffRender.Ansi.Strip(render.Split('\n')[^1]));
    }

    [Fact]
    public void EmptyInputLeftOpensHistory()
    {
        var state = new ChatTuiState();
        var action = ChatKeyMapper.Map(
            Key(ConsoleKey.LeftArrow),
            state);

        Assert.Equal(ChatKeyAction.OpenHistory, action);
    }

    [Fact]
    public void NonEmptyInputLeftMovesCursor()
    {
        var state = new ChatTuiState { Input = "abc", Cursor = 3 };
        var action = ChatKeyMapper.Map(
            Key(ConsoleKey.LeftArrow),
            state);

        Assert.Equal(ChatKeyAction.CursorLeft, action);
    }

    [Fact]
    public void HistoryKeysMapToSelectionActions()
    {
        var state = new ChatTuiState { Screen = ChatScreen.History };

        Assert.Equal(ChatKeyAction.HistoryUp, ChatKeyMapper.Map(Key(ConsoleKey.UpArrow), state));
        Assert.Equal(ChatKeyAction.HistoryDown, ChatKeyMapper.Map(Key(ConsoleKey.DownArrow), state));
        Assert.Equal(ChatKeyAction.HistoryDown, ChatKeyMapper.Map(Key(ConsoleKey.J, 'j'), state));
        Assert.Equal(ChatKeyAction.HistoryUp, ChatKeyMapper.Map(Key(ConsoleKey.K, 'k'), state));
        Assert.Equal(ChatKeyAction.HistoryPageDown, ChatKeyMapper.Map(Key(ConsoleKey.PageDown), state));
        Assert.Equal(ChatKeyAction.HistoryPageUp, ChatKeyMapper.Map(Key(ConsoleKey.PageUp), state));
        Assert.Equal(ChatKeyAction.HistoryHome, ChatKeyMapper.Map(Key(ConsoleKey.Home), state));
        Assert.Equal(ChatKeyAction.HistoryEnd, ChatKeyMapper.Map(Key(ConsoleKey.End), state));
        Assert.Equal(ChatKeyAction.HistoryOpen, ChatKeyMapper.Map(Key(ConsoleKey.Enter), state));
        Assert.Equal(ChatKeyAction.HistoryOpen, ChatKeyMapper.Map(Key(ConsoleKey.O, 'o'), state));
        Assert.Equal(ChatKeyAction.HistoryNew, ChatKeyMapper.Map(Key(ConsoleKey.N, 'n'), state));
        Assert.Equal(
            ChatKeyAction.HistoryNextGrouping,
            ChatKeyMapper.Map(Key(ConsoleKey.G, 'g'), state));
        Assert.Equal(
            ChatKeyAction.HistoryRename,
            ChatKeyMapper.Map(Key(ConsoleKey.R, 'r'), state));
        Assert.Equal(
            ChatKeyAction.HistoryStop,
            ChatKeyMapper.Map(Key(ConsoleKey.X, 'x'), state));
        Assert.Equal(
            ChatKeyAction.ToggleShortcuts,
            ChatKeyMapper.Map(Key(ConsoleKey.Oem2, '?'), state));
        Assert.Equal(
            ChatKeyAction.HistoryDelete,
            ChatKeyMapper.Map(Key(ConsoleKey.Delete), state));
        Assert.Equal(
            ChatKeyAction.HistoryDelete,
            ChatKeyMapper.Map(Key(ConsoleKey.Backspace), state));
        Assert.Equal(ChatKeyAction.HistoryNextFilter, ChatKeyMapper.Map(Key(ConsoleKey.Tab, '\t'), state));
        Assert.Equal(
            ChatKeyAction.HistoryPreviousFilter,
            ChatKeyMapper.Map(Key(ConsoleKey.Tab, '\t', shift: true), state));
        Assert.Equal(ChatKeyAction.HistorySearch, ChatKeyMapper.Map(Key(ConsoleKey.Oem2, '/'), state));
        Assert.Equal(ChatKeyAction.HistorySearch, ChatKeyMapper.Map(Key(ConsoleKey.F3), state));
        Assert.Equal(ChatKeyAction.HistoryBack, ChatKeyMapper.Map(Key(ConsoleKey.Escape), state));
    }

    [Fact]
    public void HistoryRenameExposesEditorKeys()
    {
        var rename = new ChatTuiState
        {
            Screen = ChatScreen.History,
            HistoryRenaming = true,
            HistoryRenameInput = "name",
            HistoryRenameCursor = 4
        };
        Assert.Equal(
            ChatKeyAction.HistoryRenameAccept,
            ChatKeyMapper.Map(Key(ConsoleKey.Enter), rename));
        Assert.Equal(
            ChatKeyAction.HistoryRenameCancel,
            ChatKeyMapper.Map(Key(ConsoleKey.Escape), rename));
        Assert.Equal(
            ChatKeyAction.Insert,
            ChatKeyMapper.Map(Key(ConsoleKey.X, 'x'), rename));
        Assert.Equal(
            ChatKeyAction.Backspace,
            ChatKeyMapper.Map(Key(ConsoleKey.Backspace), rename));

    }

    [Fact]
    public void HistorySearchKeysEditAndCloseSearch()
    {
        var state = new ChatTuiState
        {
            Screen = ChatScreen.History,
            HistorySearching = true
        };

        Assert.Equal(ChatKeyAction.Insert, ChatKeyMapper.Map(Key(ConsoleKey.N, 'n'), state));
        Assert.Equal(ChatKeyAction.Insert, ChatKeyMapper.Map(Key(ConsoleKey.Oem2, '/'), state));
        Assert.Equal(ChatKeyAction.Backspace, ChatKeyMapper.Map(Key(ConsoleKey.Backspace), state));
        Assert.Equal(ChatKeyAction.HistorySearchAccept, ChatKeyMapper.Map(Key(ConsoleKey.Enter), state));
        Assert.Equal(ChatKeyAction.HistorySearchClose, ChatKeyMapper.Map(Key(ConsoleKey.Escape), state));
    }

    [Fact]
    public void ControlCIsCancelOrExitEvenWithoutModifierFlags()
    {
        var state = new ChatTuiState();
        var key = new ConsoleKeyInfo('\u0003', ConsoleKey.C, shift: false, alt: false, control: false);
        Assert.Equal(ChatKeyAction.CancelOrExit, ChatKeyMapper.Map(key, state));
    }

    [Fact]
    public void CtrlCBranchesBetweenCancelCopyAndExit()
    {
        var idle = new ChatTuiState();
        Assert.Equal(CtrlCAction.Exit, ChatTuiRunner.ResolveCtrlC(idle, hasConnection: true));

        var active = new ChatTuiState { IsTurnActive = true };
        Assert.Equal(CtrlCAction.Cancel, ChatTuiRunner.ResolveCtrlC(active, hasConnection: true));

        active.ExtendSelection(-1, 4);
        Assert.True(active.HasSelection);
        Assert.Equal(
            CtrlCAction.CopySelection,
            ChatTuiRunner.ResolveCtrlC(active, hasConnection: true));

        active.ClearSelection();
        active.IsTurnActive = false;
        active.ExtendSelection(-1, 4);
        Assert.True(active.HasSelection);
        Assert.Equal(
            CtrlCAction.CopySelection,
            ChatTuiRunner.ResolveCtrlC(active, hasConnection: true));
    }

    [Fact]
    public void SelectionKeysMapAndSelectionTextIsStable()
    {
        var state = new ChatTuiState();
        state.AddInfo("one");
        state.AddInfo("two");

        Assert.Equal(
            ChatKeyAction.SelectUp,
            ChatKeyMapper.Map(Key(ConsoleKey.UpArrow, shift: true), state));
        Assert.Equal(
            ChatKeyAction.SelectDown,
            ChatKeyMapper.Map(Key(ConsoleKey.DownArrow, shift: true), state));

        state.ExtendSelection(-1, 2);
        Assert.Equal("one\ntwo", state.SelectedText(80));
        state.ClearSelection();
        Assert.False(state.HasSelection);
    }

    [Fact]
    public void CopyShortcutKeysMap()
    {
        var state = new ChatTuiState();

        Assert.Equal(
            ChatKeyAction.CopyLastAssistant,
            ChatKeyMapper.Map(
                Key(ConsoleKey.C, '\u0003', control: true, shift: true),
                state));
        Assert.Equal(
            ChatKeyAction.ClearScreen,
            ChatKeyMapper.Map(Key(ConsoleKey.L, '\u000c', control: true), state));
        Assert.Equal(
            ChatKeyAction.Exit,
            ChatKeyMapper.Map(Key(ConsoleKey.D, '\u0004', control: true), state));
        Assert.Equal(
            ChatKeyAction.ToggleReasoning,
            ChatKeyMapper.Map(Key(ConsoleKey.R, '\u0012', control: true), state));
    }

    [Fact]
    public void CopyTargetIsLastAssistantAndExcludesReasoning()
    {
        var state = new ChatTuiState();
        state.Apply(new SessionEvent(SessionEventKind.TurnStarted));
        state.Apply(new SessionEvent(
            SessionEventKind.ReasoningDelta,
            Text: "secret-thought"));
        state.Apply(new SessionEvent(
            SessionEventKind.AssistantDelta,
            Text: "answer"));
        state.Apply(new SessionEvent(
            SessionEventKind.ReasoningDelta,
            Text: "more-thought"));

        Assert.Equal("answer", state.LastAssistantText());
    }

    [Fact]
    public void ApprovalKeysMapToAllowAndDeny()
    {
        var state = new ChatTuiState { PendingApprovalId = "approval-1" };

        Assert.Equal(
            ChatKeyAction.ApprovalAllow,
            ChatKeyMapper.Map(Key(ConsoleKey.A, 'a'), state));
        Assert.Equal(
            ChatKeyAction.ApprovalDeny,
            ChatKeyMapper.Map(Key(ConsoleKey.D, 'd'), state));
    }

    [Fact]
    public void QuestionMarkOnlyOpensShortcutsWithEmptyInput()
    {
        var state = new ChatTuiState();
        Assert.Equal(
            ChatKeyAction.ToggleShortcuts,
            ChatKeyMapper.Map(Key(ConsoleKey.Oem2, '?'), state));

        state.Input = "why";
        state.Cursor = 3;
        Assert.Equal(
            ChatKeyAction.Insert,
            ChatKeyMapper.Map(Key(ConsoleKey.Oem2, '?'), state));
    }

    [Fact]
    public void ReasoningMergesAndCanBeHidden()
    {
        var state = new ChatTuiState();
        state.Apply(new SessionEvent(
            SessionEventKind.ReasoningDelta,
            Text: "sec"));
        state.Apply(new SessionEvent(
            SessionEventKind.ReasoningDelta,
            Text: "ret"));

        var reasoning = Assert.Single(
            state.Transcript,
            entry => entry.Kind == TranscriptKind.Reasoning);
        Assert.Equal("secret", reasoning.Text);
        var visible = ChatTuiRenderer.Render(100, 24, state);
        Assert.Contains("Thinking… secret", visible);

        state.ToggleReasoning();
        var hidden = ChatTuiRenderer.Render(100, 24, state);
        Assert.Contains("Thinking… (Ctrl+R to show)", hidden);
        Assert.DoesNotContain("secret", hidden);

        state.ToggleReasoning();
        Assert.Contains("Thinking… secret", ChatTuiRenderer.Render(100, 24, state));
    }

    [Fact]
    public void DiffLinesAreColoredByKind()
    {
        var state = new ChatTuiState();
        state.Transcript.Add(new TranscriptEntry(
            TranscriptKind.Diff,
            "+new\n-old\n@@ hunk\n context"));

        var lines = ChatTuiRenderer.TranscriptLines(state, 100);

        Assert.Contains(lines, line => line.Contains("[diff]"));
        Assert.Contains(lines, line =>
            line.Contains(DiffRender.Ansi.Green) && line.Contains("+new"));
        Assert.Contains(lines, line =>
            line.Contains(DiffRender.Ansi.Red) && line.Contains("-old"));
        Assert.Contains(lines, line =>
            line.Contains(DiffRender.Ansi.BoldCyan) && line.Contains("@@ hunk"));
    }

    [Fact]
    public void ApprovalCardRedactsSecretsAndHighlightsChoices()
    {
        var state = new ChatTuiState();
        state.Apply(new SessionEvent(
            SessionEventKind.ApprovalRequested,
            Name: "shell",
            ApprovalId: "a1",
            Action: "run",
            Resource: "cargo test --api_key=super-secret",
            Reason: "executes a command"));

        var render = ChatTuiRenderer.Render(100, 24, state);

        Assert.Contains("[approval]", render);
        Assert.Contains("tool: shell", render);
        Assert.Contains("command/path: run cargo test --api_key=***", render);
        Assert.Contains("risk: executes a command", render);
        Assert.Contains("[a] allow", render);
        Assert.Contains("[d] deny", render);
        Assert.DoesNotContain("super-secret", render);
    }

    [Fact]
    public void LongToolOutputIsTruncatedWithOmittedCount()
    {
        var state = new ChatTuiState();
        var text = string.Join(
            "\n",
            Enumerable.Range(0, 30).Select(index => $"line-{index}"));
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.Tool, text));

        var render = ChatTuiRenderer.Render(100, 40, state);

        Assert.Contains("line-0", render);
        Assert.Contains("line-19", render);
        Assert.DoesNotContain("line-20", render);
        Assert.Contains("(10 lines omitted)", render);
    }

    [Fact]
    public void ModelPickerShowsProtocolContextAndThreshold()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "models": [
                {
                  "provider": "local",
                  "model": "gpt-test",
                  "protocol": "openai-responses",
                  "context_window": 256000,
                  "compact_threshold": 0.9,
                  "enabled": true
                },
                {
                  "provider": "local",
                  "model": "disabled",
                  "protocol": "openai-chat",
                  "context_window": 1000,
                  "compact_threshold": 0.5,
                  "enabled": false
                }
              ]
            }
            """);

        var models = ChatModelPicker.Parse(document.RootElement);
        Assert.Single(models);
        var picker = new ChatModelPickerState();
        picker.Open(models, "local", "gpt-test");
        var lines = ChatModelPicker.Render(
            picker,
            100,
            10,
            UiLanguage.English);

        Assert.Contains(lines, line => line.Contains("protocol=openai-responses"));
        Assert.Contains(lines, line => line.Contains("context_window=256000"));
        Assert.Contains(lines, line => line.Contains("compact_threshold=0.9"));
        Assert.StartsWith("› ", DiffRender.Ansi.Strip(lines[1]));
    }

    [Fact]
    public void ToolOutputStartsOnANewLine()
    {
        var state = new ChatTuiState();
        state.Apply(new SessionEvent(
            SessionEventKind.ToolStarted,
            Name: "read_file",
            Args: "{\"path\":\"a.txt\"}"));
        state.Apply(new SessionEvent(
            SessionEventKind.ToolOutput,
            Text: "contents"));

        Assert.Equal("read_file {\"path\":\"a.txt\"}\ncontents", state.Transcript[^1].Text);
    }

    [Fact]
    public void ParsesMissingHistoryFieldsAsEmpty()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "sessions": [
                {
                  "session_id": "s1",
                  "workspace": "D:\\work",
                  "provider": "openai",
                  "model": "gpt",
                  "created_at": "2026-10-17T10:00:00Z",
                  "updated_at": "2026-10-17T11:00:00Z",
                  "message_count": 2
                }
              ]
            }
            """);

        var sessions = ChatTuiState.ParseSessions(document.RootElement);

        var session = Assert.Single(sessions);
        Assert.Null(session.Status);
        Assert.Null(session.Task);
        Assert.Null(session.Prompt);
        Assert.Null(session.LastMessage);
    }

    [Fact]
    public void ParsesHistoryAndReplaysVisibleTranscript()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "messages": [
                {"role": "user", "text": "fix it", "timestamp": "2026-10-17T10:00:00Z", "seq": 2},
                {
                  "role": "tool",
                  "text": "",
                  "timestamp": "2026-10-17T10:00:01Z",
                  "seq": 4,
                  "tool": {"name": "shell", "ok": true, "summary": "exit 0 in 5 ms"}
                },
                {"role": "assistant", "text": "done", "timestamp": "2026-10-17T10:00:02Z", "seq": 5}
              ]
            }
            """);

        var messages = ChatTuiState.ParseHistory(document.RootElement);
        var state = new ChatTuiState();
        state.AddHistory(messages);

        Assert.Equal(3, messages.Count);
        Assert.Equal(2, messages[0].Seq);
        Assert.Equal(
            new[] { TranscriptKind.User, TranscriptKind.Tool, TranscriptKind.Assistant },
            state.Transcript.Select(entry => entry.Kind));
        Assert.Equal("fix it", state.Transcript[0].Text);
        Assert.Equal("[ok] shell: exit 0 in 5 ms", state.Transcript[1].Text);
        Assert.Equal("done", state.Transcript[2].Text);
    }

    [Fact]
    public void MissingHistoryFieldsStayEmpty()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "messages": [
                {"role": "assistant", "text": "hi"}
              ]
            }
            """);

        var message = Assert.Single(ChatTuiState.ParseHistory(document.RootElement));
        Assert.Equal(string.Empty, message.Timestamp);
        Assert.Null(message.Seq);
        Assert.Null(message.ToolName);
        Assert.Null(message.ToolOk);
        Assert.Null(message.ToolSummary);
    }

    [Fact]
    public void LiveAssistantDeltaDoesNotMergeIntoReplayedMessage()
    {
        var state = new ChatTuiState();
        state.AddHistory(
        [
            new HistoryMessage("assistant", "old answer", string.Empty, 1, null, null, null)
        ]);

        state.Apply(new SessionEvent(SessionEventKind.TurnStarted));
        state.Apply(new SessionEvent(SessionEventKind.AssistantDelta, Text: "new answer"));

        Assert.Equal(2, state.Transcript.Count);
        Assert.Equal("old answer", state.Transcript[0].Text);
        Assert.Equal("new answer", state.Transcript[1].Text);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    public void ChatAnsiDoesNotAffectLayoutWidth(int width)
    {
        var state = new ChatTuiState("D:\\kl", "openai", "gpt-5", "s1")
        {
            ReasoningEffort = "high"
        };
        state.Settings.SetLanguage(UiLanguage.ChineseSimplified);
        state.Transcript.Add(new TranscriptEntry(
            TranscriptKind.User,
            "请修复一个很长的中文任务，确保消息块在窄终端中正确换行。"));
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.Assistant, "完成。"));
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.Tool, "[ok] shell: exit 0"));
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.Diff, "-old\n+new"));
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.Error, "示例错误"));

        var render = ChatTuiRenderer.Render(width, 24, state);

        Assert.Contains("\u001b[", render);
        Assert.Equal(24, render.Split('\n').Length);
        Assert.All(render.Split('\n'), line =>
            Assert.True(DiffRender.Ansi.VisibleWidth(line) <= width));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    public void ShortcutsPageFitsWidth(int width)
    {
        var state = new ChatTuiState("D:\\kl", "openai", "gpt-5", "s1")
        {
            Screen = ChatScreen.Shortcuts,
            ShortcutReturnScreen = ChatScreen.Chat
        };

        var render = ChatTuiRenderer.Render(width, 24, state);

        Assert.All(render.Split('\n'), line =>
            Assert.True(DiffRender.Ansi.VisibleWidth(line) <= width));
    }
    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    public void HomeFitsWidth(int width)
    {
        var state = new ChatTuiState();
        state.Settings.SetLanguage(UiLanguage.ChineseSimplified);

        var render = ChatTuiRenderer.Render(width, 24, state);

        Assert.Contains(
            "/init - 创建 AGENTS.md 并写入 KL 指令",
            DiffRender.Ansi.Strip(render));
        Assert.All(render.Split('\n'), line =>
            Assert.True(DiffRender.Ansi.VisibleWidth(line) <= width));
    }

    private static string Snapshot(string render) =>
        string.Join(
            '\n',
            DiffRender.Ansi.Strip(render)
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Split('\n')
                .Select(line => line.TrimEnd()));

    private static ConsoleKeyInfo Key(
        ConsoleKey key,
        char character = '\0',
        bool control = false,
        bool shift = false) =>
        new(character, key, shift, alt: false, control);
}

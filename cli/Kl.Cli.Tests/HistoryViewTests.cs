using Xunit;

namespace Kl.Cli.Tests;

public sealed class HistoryViewTests
{
    [Fact]
    public void GroupsTasksByWorkspaceWithVisibleCounts()
    {
        var view = HistoryView.Build(
            [
                Session("s1", "D:\\a", "ready", "first"),
                Session("s2", "D:\\a", "completed", "second"),
                Session("s3", "D:\\b", "ready", "third")
            ],
            HistoryFilter.All,
            string.Empty,
            string.Empty,
            hasPendingApproval: false,
            isTurnActive: false);

        Assert.Equal(2, view.Groups.Count);
        Assert.Equal("D:\\a", view.Groups[0].Label);
        Assert.Equal(2, view.Groups[0].Tasks.Count);
        Assert.Equal("D:\\b", view.Groups[1].Label);
        Assert.Single(view.Groups[1].Tasks);
        Assert.Equal(3, view.Count(HistoryFilter.All));
    }

    [Fact]
    public void StatusCountsUseCurrentSessionConcurrency()
    {
        var sessions = new[]
        {
            Session("current", "D:\\kl", "completed", "current"),
            Session("ready", "D:\\kl", "ready", "ready"),
            Session("failed", "D:\\kl", "failed", "failed"),
            Session("unknown", "D:\\kl", null, "unknown")
        };

        var working = HistoryView.Build(
            sessions,
            HistoryFilter.All,
            string.Empty,
            "current",
            hasPendingApproval: false,
            isTurnActive: true);
        Assert.Equal(1, working.Count(HistoryFilter.Working));
        Assert.Equal(2, working.Count(HistoryFilter.Ready));
        Assert.Equal(1, working.Count(HistoryFilter.Inactive));

        var needsYou = HistoryView.Build(
            sessions,
            HistoryFilter.All,
            string.Empty,
            "current",
            hasPendingApproval: true,
            isTurnActive: true);
        Assert.Equal(1, needsYou.Count(HistoryFilter.NeedsYou));
        Assert.Equal(2, needsYou.Count(HistoryFilter.Ready));
        Assert.Equal(1, needsYou.Count(HistoryFilter.Inactive));

        var ready = HistoryView.Build(
            sessions,
            HistoryFilter.Ready,
            string.Empty,
            "current",
            hasPendingApproval: false,
            isTurnActive: false);
        Assert.All(ready.Tasks, task => Assert.Equal(HistoryTaskStatus.Ready, task.Status));
        Assert.Equal(2, ready.Tasks.Count);
    }

    [Fact]
    public void SearchMatchesAllFieldsCaseInsensitivelyAndChinese()
    {
        var sessions = new[]
        {
            Session(
                "s1",
                "D:\\alpha",
                "ready",
                "Fix LOGIN",
                prompt: "请修复登录问题",
                last: "build passed",
                provider: "local",
                model: "gpt-test")
        };

        foreach (var query in new[]
                 {
                     "login", "修复登录", "alpha", "local", "GPT-TEST", "passed"
                 })
        {
            var view = HistoryView.Build(
                sessions,
                HistoryFilter.All,
                query,
                string.Empty,
                hasPendingApproval: false,
                isTurnActive: false);
            Assert.Single(view.Tasks);
        }

        var empty = HistoryView.Build(
            sessions,
            HistoryFilter.All,
            "missing",
            string.Empty,
            hasPendingApproval: false,
            isTurnActive: false);
        Assert.Empty(empty.Tasks);
        Assert.Empty(empty.Groups);
    }

    [Fact]
    public void SelectionMovesAcrossTasksButNotGroupHeaders()
    {
        var view = HistoryView.Build(
            [
                Session("s1", "D:\\a", "ready", "one"),
                Session("s2", "D:\\a", "completed", "two"),
                Session("s3", "D:\\b", "ready", "three")
            ],
            HistoryFilter.All,
            string.Empty,
            string.Empty,
            hasPendingApproval: false,
            isTurnActive: false);

        var selection = HistoryView.MoveSelection(0, 1, view.Tasks.Count);
        Assert.Equal("s2", view.Selected(selection)?.Session.SessionId);
        selection = HistoryView.MoveSelection(selection, 1, view.Tasks.Count);
        Assert.Equal("s3", view.Selected(selection)?.Session.SessionId);
        Assert.Equal(2, selection);
    }

    [Fact]
    public void FilterCyclesAndSwitchingResetsSelection()
    {
        var state = new ChatTuiState
        {
            HistorySelection = 3
        };

        Assert.Equal(
            HistoryFilter.NeedsYou,
            HistoryView.MoveFilter(HistoryFilter.All, 1));
        Assert.Equal(
            HistoryFilter.Inactive,
            HistoryView.MoveFilter(HistoryFilter.All, -1));

        state.SetHistoryFilter(HistoryFilter.Ready);
        Assert.Equal(HistoryFilter.Ready, state.HistoryFilter);
        Assert.Equal(0, state.HistorySelection);
    }

    [Fact]
    public void GroupingCyclesAndRegroupsTasks()
    {
        var state = new ChatTuiState
        {
            HistorySelection = 2
        };
        Assert.Equal(
            HistoryGrouping.Status,
            HistoryView.MoveGrouping(HistoryGrouping.Workspace, 1));
        Assert.Equal(
            HistoryGrouping.Model,
            HistoryView.MoveGrouping(HistoryGrouping.Status, 1));
        Assert.Equal(
            HistoryGrouping.Workspace,
            HistoryView.MoveGrouping(HistoryGrouping.Model, 1));

        state.SetHistoryGrouping(HistoryGrouping.Model);
        Assert.Equal(HistoryGrouping.Model, state.HistoryGrouping);
        Assert.Equal(0, state.HistorySelection);

        var view = HistoryView.Build(
            [
                Session("s1", "D:\\a", "ready", "one", model: "m1"),
                Session("s2", "D:\\b", "completed", "two", model: "m2"),
                Session("s3", "D:\\c", "ready", "three", model: "m1")
            ],
            HistoryFilter.All,
            string.Empty,
            string.Empty,
            hasPendingApproval: false,
            isTurnActive: false,
            grouping: HistoryGrouping.Model);
        Assert.Equal(2, view.Groups.Count);
        Assert.Equal("local/m1", view.Groups[0].Label);
        Assert.Equal(2, view.Groups[0].Tasks.Count);
        Assert.Equal("local/m2", view.Groups[1].Label);
    }

    [Fact]
    public void SearchEditingFiltersInRealTimeAndClearsOnEscape()
    {
        var state = new ChatTuiState
        {
            Screen = ChatScreen.History,
            HistorySelection = 1,
            History =
            [
                Session("s1", "D:\\a", "ready", "Fix login"),
                Session("s2", "D:\\a", "ready", "Write docs")
            ]
        };

        state.StartHistorySearch();
        foreach (var character in "LOG")
        {
            state.InsertHistorySearch(character);
        }

        Assert.True(state.HistorySearching);
        Assert.Single(state.GetHistoryView().Tasks);

        state.CloseHistorySearch(clear: true);
        Assert.False(state.HistorySearching);
        Assert.Empty(state.HistorySearch);
        Assert.Equal(2, state.GetHistoryView().Tasks.Count);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    public void HistoryRenderNeverExceedsTerminalWidth(int width)
    {
        var state = new ChatTuiState
        {
            Screen = ChatScreen.History,
            History =
            [
                Session(
                    "s1",
                    "D:\\workspace-with-a-very-long-name",
                    "ready",
                    "A very long task title that should be truncated cleanly",
                    prompt: "请修复一个很长的中文提示，确保搜索框和任务行都不会溢出。",
                    last: "done"),
                Session("s2", "D:\\kl", "completed", string.Empty)
            ]
        };

        var render = ChatTuiRenderer.Render(width, 24, state);

        foreach (var line in render.Split('\n'))
        {
            Assert.True(
                DisplayText.Width(DiffRender.Ansi.Strip(line)) <= width,
                $"line wider than {width}: {line}");
        }
    }

    [Fact]
    public void SearchBoxUsesDisplayWidthForChinese()
    {
        var state = new ChatTuiState
        {
            Screen = ChatScreen.History,
            HistorySearching = true,
            HistorySearch = "请搜索一个非常长的中文任务名称",
            HistorySearchCursor = 15,
            History = [Session("s1", "D:\\kl", "ready", "task one")]
        };
        state.Settings.SetLanguage(UiLanguage.ChineseSimplified);

        var render = ChatTuiRenderer.Render(40, 24, state);
        var searchLine = render.Split('\n').First(line => line.Contains("搜索 ›"));

        Assert.Contains("|", searchLine);
        Assert.True(DisplayText.Width(DiffRender.Ansi.Strip(searchLine)) <= 40);

        var rawLines = render.Split('\n');
        var lines = rawLines
            .Select(line => DiffRender.Ansi.Strip(line).TrimEnd())
            .ToList();
        var titleRow = lines.FindIndex(line => line.Contains("历史", StringComparison.Ordinal));
        var tabRow = lines.FindIndex(line => line.Contains("全部 1", StringComparison.Ordinal));
        var separatorRow = lines.FindIndex(line =>
            line.Length > 0 && line.All(character => character == '─'));
        var searchRow = lines.FindIndex(line => line.Contains("搜索 ›", StringComparison.Ordinal));
        Assert.True(titleRow < tabRow);
        Assert.True(tabRow < separatorRow);
        Assert.True(separatorRow < searchRow);
        Assert.Contains(DiffRender.Ansi.ActiveTab, rawLines[searchRow]);
        Assert.Contains(DiffRender.Ansi.ResetForeground, rawLines[searchRow]);
    }

    [Fact]
    public void HistorySearchKeepsTabsAndTableHeaderVisible()
    {
        var state = new ChatTuiState
        {
            Screen = ChatScreen.History,
            HistorySearching = true,
            HistorySearch = "task",
            HistorySearchCursor = 4,
            History = [Session("s1", "D:\\kl", "ready", "task one")]
        };

        var lines = DiffRender.Ansi.Strip(ChatTuiRenderer.Render(80, 24, state))
            .Split('\n')
            .Select(line => line.TrimEnd())
            .ToList();

        var titleRow = lines.FindIndex(line => line.Contains("History", StringComparison.Ordinal));
        var tabRow = lines.FindIndex(line => line.Contains("All 1", StringComparison.Ordinal));
        var separatorRow = lines.FindIndex(line =>
            line.Length > 0 && line.All(character => character == '─'));
        var searchRow = lines.FindIndex(line => line.Contains("Search ›", StringComparison.Ordinal));
        var tableRow = lines.FindIndex(line => line.Contains("Tasks", StringComparison.Ordinal));
        Assert.True(titleRow < tabRow);
        Assert.True(tabRow < separatorRow);
        Assert.True(separatorRow < searchRow);
        Assert.True(searchRow < tableRow);
    }
    [Fact]
    public void HistoryUsesHomeShorthandForWorkspacePaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var workspace = Path.Combine(home, "project");
        var state = new ChatTuiState
        {
            Screen = ChatScreen.History,
            HistorySelection = 0,
            History = [Session("s1", workspace, "ready", "home task")]
        };

        var plain = DiffRender.Ansi.Strip(ChatTuiRenderer.Render(120, 24, state));

        Assert.Contains("~" + Path.DirectorySeparatorChar + "project", plain);
        Assert.DoesNotContain(home, plain, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WideHistoryUsesTwoColumnsAndNarrowHistoryStacksDetails()
    {
        var state = new ChatTuiState
        {
            Screen = ChatScreen.History,
            HistorySelection = 1,
            History =
            [
                Session("s1", "D:\\kl", "ready", "task one"),
                Session("s2", "D:\\kl", "ready", "task two")
            ]
        };

        var wide = ChatTuiRenderer.Render(120, 24, state);
        var widePlain = DiffRender.Ansi.Strip(wide);
        Assert.Contains("History", widePlain);
        Assert.Contains("Task details", widePlain);

        var narrow = ChatTuiRenderer.Render(80, 24, state);
        var narrowLines = DiffRender.Ansi.Strip(narrow).Split('\n');
        var taskLine = Array.FindIndex(narrowLines, line => line.Contains("task one"));
        var detailLine = Array.FindIndex(narrowLines, line => line.Contains("Task details"));
        Assert.True(taskLine >= 0);
        Assert.True(detailLine > taskLine);
    }

    [Fact]
    public void HistoryRendersChineseLabelsAndUntitledTask()
    {
        var state = new ChatTuiState
        {
            Screen = ChatScreen.History,
            HistorySelection = 1,
            History = [Session("s1", "D:\\kl", "ready", null)]
        };
        state.Settings.SetLanguage(UiLanguage.ChineseSimplified);

        var render = ChatTuiRenderer.Render(120, 24, state);
        var plain = DiffRender.Ansi.Strip(render);

        Assert.Contains("历史任务  分组：工作区", plain);
        Assert.Contains("全部 1", plain);
        Assert.Contains("未命名任务", plain);
        Assert.Contains("任务详情", plain);
        Assert.Contains("○ 就绪", plain);
        Assert.Contains("模型等级：high", plain);
        Assert.Contains("Tab/Shift+Tab 切换", plain);
        Assert.DoesNotContain(">_ KL (v0.1.0)", plain);
        var plainLines = plain.Split('\n');
        Assert.Contains("分组：工作区  g", plainLines[0].TrimEnd());
        Assert.EndsWith(
            "Tab/Shift+Tab 切换",
            plainLines.First(line => line.Contains("全部 1")).TrimEnd());
        Assert.Contains(
            "delete 删除",
            DiffRender.Ansi.Strip(render.Split('\n')[^1]));
        Assert.DoesNotContain(
            "Tab",
            DiffRender.Ansi.Strip(render.Split('\n')[^1]));
    }

    [Fact]
    public void HistoryShowsStatusTabsWorkspaceGroupingAndEllipsis()
    {
        var state = new ChatTuiState
        {
            Screen = ChatScreen.History,
            HistorySelection = 1,
            History =
            [
                Session(
                    "s1",
                    "D:\\workspace-with-a-very-long-name",
                    "ready",
                    "A task title that is intentionally much longer than the row"),
                Session("s2", "D:\\kl", "completed", "inactive task")
            ]
        };

        var plain = DiffRender.Ansi.Strip(ChatTuiRenderer.Render(60, 24, state));

        Assert.Contains("Group: Workspace", plain);
        Assert.Contains("All 2", plain);
        Assert.Contains("Needs you 0", plain);
        Assert.Contains("Working 0", plain);
        Assert.Contains("Ready 1", plain);
        Assert.Contains("Inactive 1", plain);
        Assert.Contains("D:\\workspace-with-a-very-long-name  1", plain);
        Assert.Contains("…", plain);
    }
    [Fact]
    public void WorkspaceGroupCenterTruncatesLongPaths()
    {
        var workspace = "D:\\" + new string('a', 80) + "\\project";
        var state = new ChatTuiState
        {
            Screen = ChatScreen.History,
            HistorySelection = 0,
            History = [Session("s1", workspace, "ready", "long path")]
        };

        var lines = DiffRender.Ansi.Strip(ChatTuiRenderer.Render(60, 24, state))
            .Split('\n');
        var groupLine = lines.First(line => line.Contains("D:\\aaa", StringComparison.Ordinal));

        Assert.Contains("…", groupLine);
        Assert.Contains("project  1", groupLine);
    }
    [Fact]
    public void HistoryShowsScrollIndicatorsForLongLists()
    {
        var state = new ChatTuiState
        {
            Screen = ChatScreen.History,
            HistorySelection = 15,
            History = Enumerable.Range(0, 20)
                .Select(index => Session(
                    $"s{index}",
                    "D:\\kl",
                    "ready",
                    $"task {index:00}"))
                .ToList()
        };

        var plain = DiffRender.Ansi.Strip(ChatTuiRenderer.Render(80, 16, state));

        Assert.Contains("↑", plain);
        Assert.Contains("↓", plain);
        Assert.Contains("task 15", plain);
    }
    [Fact]
    public void HistoryUsesCodexSelectionAndActiveTabColors()
    {
        var state = new ChatTuiState
        {
            Screen = ChatScreen.History,
            HistorySelection = 1,
            History = [Session("s1", "D:\\kl", "ready", "task one")]
        };

        var render = ChatTuiRenderer.Render(120, 24, state);

        Assert.Contains(DiffRender.Ansi.Selection, render);
        Assert.Contains(DiffRender.Ansi.ActiveTab, render);
        Assert.Contains("\u001b[38;5;", render);
        Assert.DoesNotContain(">_ KL (v0.1.0)", render);
    }

    [Fact]
    public void HistoryRenameShortcutsPageAndFooterOrder()
    {
        var state = new ChatTuiState
        {
            Screen = ChatScreen.History,
            HistorySelection = 1,
            History = [Session("s1", "D:\\kl", "ready", "task one")],
            HistoryRenaming = true,
            HistoryRenameSessionId = "s1",
            HistoryRenameInput = "new name",
            HistoryRenameCursor = 3
        };
        state.Settings.SetLanguage(UiLanguage.ChineseSimplified);

        var rename = DiffRender.Ansi.Strip(ChatTuiRenderer.Render(100, 24, state));
        Assert.Contains("重命名 › new| name", rename);
        Assert.Contains("Enter 保存", rename.Split('\n')[^1]);
        var renameLines = rename.Split('\n')
            .Select(line => line.TrimEnd())
            .ToList();
        var renameTitle = renameLines.FindIndex(line => line.Contains("历史", StringComparison.Ordinal));
        var renameSeparator = renameLines.FindIndex(line =>
            line.Length > 0 && line.All(character => character == '─'));
        var renameEditor = renameLines.FindIndex(line => line.Contains("重命名 ›", StringComparison.Ordinal));
        Assert.True(renameTitle < renameSeparator);
        Assert.True(renameSeparator < renameEditor);

        state.HistoryRenaming = false;
        state.Screen = ChatScreen.Shortcuts;
        state.ShortcutReturnScreen = ChatScreen.History;
        var shortcuts = DiffRender.Ansi.Strip(ChatTuiRenderer.Render(100, 24, state));
        Assert.Contains("历史页快捷键", shortcuts);
        Assert.Contains("导航", shortcuts);
        Assert.Contains("任务", shortcuts);
        Assert.Contains("视图", shortcuts);
        Assert.Contains("重命名任务", shortcuts);
        Assert.Contains("停止任务", shortcuts);
        Assert.DoesNotContain("┌", shortcuts);
        Assert.EndsWith("Esc 返回", shortcuts.Split('\n')[^1].TrimEnd());

        state.Screen = ChatScreen.History;
        state.StatusMessage = "任务已删除";
        var footer = DiffRender.Ansi.Strip(ChatTuiRenderer.Render(100, 24, state).Split('\n')[^1]);
        Assert.StartsWith("r 重命名", footer.TrimStart());
        Assert.True(footer.IndexOf("r 重命名") < footer.IndexOf("任务已删除"));
        Assert.EndsWith("任务已删除", footer.TrimEnd());
    }

    private static HistorySession Session(
        string id,
        string workspace,
        string? status,
        string? task,
        string? prompt = null,
        string? last = null,
        string provider = "local",
        string model = "gpt-test") =>
        new(
            id,
            workspace,
            provider,
            model,
            DateTimeOffset.UtcNow.AddSeconds(-2).ToString("O"),
            2,
            status,
            task,
            prompt,
            last ?? "done",
            "high");
}

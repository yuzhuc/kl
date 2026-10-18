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
        Assert.Equal("D:\\a", view.Groups[0].Workspace);
        Assert.Equal(2, view.Groups[0].Tasks.Count);
        Assert.Equal("D:\\b", view.Groups[1].Workspace);
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
    public void SearchEditingFiltersInRealTimeAndClearsOnEscape()
    {
        var state = new ChatTuiState
        {
            Screen = ChatScreen.History,
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
    }

    [Fact]
    public void WideHistoryUsesTwoColumnsAndNarrowHistoryStacksDetails()
    {
        var state = new ChatTuiState
        {
            Screen = ChatScreen.History,
            History = [Session("s1", "D:\\kl", "ready", "task one")]
        };

        var wide = ChatTuiRenderer.Render(120, 24, state);
        Assert.Contains(
            wide.Split('\n'),
            line => line.Contains("History") && line.Contains("Task details"));

        var narrow = ChatTuiRenderer.Render(80, 24, state);
        var narrowLines = narrow.Split('\n');
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
            History = [Session("s1", "D:\\kl", "ready", null)]
        };
        state.Settings.SetLanguage(UiLanguage.ChineseSimplified);

        var render = ChatTuiRenderer.Render(120, 24, state);

        Assert.Contains("历史  分组：工作区", render);
        Assert.Contains("全部 1", render);
        Assert.Contains("未命名任务", render);
        Assert.Contains("任务详情", render);
        Assert.Contains("状态：就绪", render);
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
            last ?? "done");
}
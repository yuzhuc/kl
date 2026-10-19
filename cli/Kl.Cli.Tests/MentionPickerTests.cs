using Xunit;

namespace Kl.Cli.Tests;

public sealed class MentionPickerTests
{
    [Fact]
    public void AtStartOpensAndFiltersFiles()
    {
        using var workspace = new TempWorkspace();
        workspace.File("README.md", "readme");
        workspace.File("src/Program.cs", "code");

        var state = new ChatTuiState(workspace.Path);
        state.Insert('@');

        Assert.True(MentionPicker.IsOpen(state));
        Assert.Contains("src/Program.cs", state.MentionPicker.Matches);

        state.Insert('s');
        Assert.Contains("src/Program.cs", state.MentionPicker.Matches);
        Assert.DoesNotContain("README.md", state.MentionPicker.Matches);
    }

    [Fact]
    public void AtAfterWhitespaceOpensButEmailDoesNot()
    {
        var state = new ChatTuiState();
        state.Input = "hello ";
        state.Cursor = state.Input.Length;

        state.Insert('@');
        Assert.True(MentionPicker.IsOpen(state));

        state.Input = "foo@bar";
        state.Cursor = state.Input.Length;
        MentionPicker.Update(state);
        Assert.False(MentionPicker.IsOpen(state));
    }

    [Fact]
    public void EnterInsertsSelectedPathAndKeepsCursorEditable()
    {
        using var workspace = new TempWorkspace();
        workspace.File("src/Program.cs", "code");

        var state = new ChatTuiState(workspace.Path);
        state.Insert('@');
        state.Insert('p');

        Assert.True(MentionPicker.Accept(state));
        Assert.Equal("@src/Program.cs ", state.Input);
        Assert.Equal(state.Input.Length, state.Cursor);
        Assert.False(MentionPicker.IsOpen(state));
    }

    [Fact]
    public void EscapeDismissesAndKeepsInput()
    {
        using var workspace = new TempWorkspace();
        workspace.File("a.txt", "a");

        var state = new ChatTuiState(workspace.Path);
        state.Insert('@');
        MentionPicker.Dismiss(state);

        Assert.False(MentionPicker.IsOpen(state));
        Assert.Equal("@", state.Input);
    }

    [Fact]
    public void IgnoredDirectoriesAreNotScanned()
    {
        using var workspace = new TempWorkspace();
        workspace.File("src/Program.cs", "code");
        workspace.File("target/generated.cs", "code");
        workspace.File("node_modules/pkg/index.js", "code");
        workspace.File("bin/tool.exe", "code");

        var files = MentionPicker.LoadFiles(workspace.Path);

        Assert.Contains("src/Program.cs", files);
        Assert.DoesNotContain("target/generated.cs", files);
        Assert.DoesNotContain("node_modules/pkg/index.js", files);
        Assert.DoesNotContain("bin/tool.exe", files);
    }

    [Fact]
    public void EmptyMatchesShowLocalizedMessage()
    {
        using var workspace = new TempWorkspace();
        workspace.File("a.txt", "a");
        var state = new ChatTuiState(workspace.Path);
        state.Settings.SetLanguage(UiLanguage.ChineseSimplified);
        state.Insert('@');
        state.Insert('z');

        Assert.Empty(state.MentionPicker.Matches);
        Assert.Contains(
            "未找到文件",
            string.Join('\n', ChatTuiRenderer.MentionPickerLines(state, 80)));
    }

    [Fact]
    public void RendererShowsMentionsPanelAndFitsWidth()
    {
        using var workspace = new TempWorkspace();
        for (var index = 0; index < 12; index++)
        {
            workspace.File($"src/file{index:00}.cs", "code");
        }

        var state = new ChatTuiState(workspace.Path);
        state.Input = "@";
        state.Cursor = 1;
        MentionPicker.Update(state);

        var lines = ChatTuiRenderer.MentionPickerLines(state, 80);
        var plain = string.Join('\n', lines.Select(DiffRender.Ansi.Strip));
        var narrow = ChatTuiRenderer.MentionPickerLines(state, 40);

        Assert.Equal(17, lines.Count);
        Assert.Contains("Mentions", plain);
        Assert.Contains("Files and directories", plain);
        Assert.Contains("All Results", plain);
        Assert.Contains("Filesystem Only", plain);
        Assert.Contains("Type to search mentions", plain);
        Assert.Contains("enter/tab insert · esc close · ↑/↓ select · ←/→ filter", plain);
        var rendered = string.Join('\n', lines);
        Assert.Contains(DiffRender.Ansi.Selection, rendered);
        Assert.Contains(DiffRender.Ansi.ActiveTab, rendered);
        Assert.Contains(DiffRender.Ansi.Cyan, rendered);
        Assert.All(lines, line => Assert.True(DiffRender.Ansi.VisibleWidth(line) <= 80));
        Assert.All(narrow, line => Assert.True(DiffRender.Ansi.VisibleWidth(line) <= 40));
    }

    [Fact]
    public void MovingUpFromLastMovesHighlightBeforeScrollingWindow()
    {
        using var workspace = new TempWorkspace();
        for (var index = 0; index < 12; index++)
        {
            workspace.File($"src/file{index:00}.cs", "code");
        }

        var state = new ChatTuiState(workspace.Path);
        state.Input = "@";
        state.Cursor = 1;
        MentionPicker.Update(state);
        state.MentionPicker.Selection = state.MentionPicker.Matches.Count - 1;

        var bottomLines = ChatTuiRenderer.MentionPickerLines(state, 80);
        var bottomRow = bottomLines.FindIndex(line =>
            line.StartsWith(DiffRender.Ansi.Selection, StringComparison.Ordinal));
        Assert.True(bottomRow >= 0);

        state.MentionPicker.Selection--;
        var upwardLines = ChatTuiRenderer.MentionPickerLines(state, 80);
        var upwardRow = upwardLines.FindIndex(line =>
            line.StartsWith(DiffRender.Ansi.Selection, StringComparison.Ordinal));
        Assert.Equal(bottomRow - 1, upwardRow);
    }
    [Fact]
    public void MovingDownScrollsAtSecondToLastVisibleRow()
    {
        using var workspace = new TempWorkspace();
        for (var index = 0; index < 12; index++)
        {
            workspace.File($"src/file{index:00}.cs", "code");
        }

        var state = new ChatTuiState(workspace.Path);
        state.Input = "@";
        state.Cursor = 1;
        MentionPicker.Update(state);
        var lines = ChatTuiRenderer.MentionPickerLines(state, 80);
        for (var step = 0; step < MentionPicker.MaxVisible; step++)
        {
            MentionPicker.MoveSelection(state, 1);
            lines = ChatTuiRenderer.MentionPickerLines(state, 80);
        }

        var selectedRow = lines.FindIndex(line =>
            line.StartsWith(DiffRender.Ansi.Selection, StringComparison.Ordinal));
        Assert.Equal(7 + MentionPicker.MaxVisible - 2, selectedRow);
        Assert.Equal(2, state.MentionPicker.Scroll);
    }
    [Fact]
    public void ClickingAMentionItemFillsTheComposer()
    {
        using var workspace = new TempWorkspace();
        workspace.File("src/file00.cs", "code");
        var state = new ChatTuiState(workspace.Path);
        state.Input = "@";
        state.Cursor = 1;
        MentionPicker.Update(state);
        var lines = DiffRender.Ansi.Strip(ChatTuiRenderer.Render(80, 24, state))
            .Split('\n');
        var row = Array.FindIndex(
            lines,
            line => line.Contains("file00.cs", StringComparison.Ordinal));
        Assert.True(row >= 0);

        Assert.True(ChatTuiRenderer.TryClickPopupItemAt(state, 80, 24, row));
        Assert.Equal("@src/file00.cs ", state.Input);
        Assert.Equal(state.Input.Length, state.Cursor);
        Assert.False(MentionPicker.IsOpen(state));
    }
    [Fact]
    public void SelectionWraps()
    {
        using var workspace = new TempWorkspace();
        workspace.File("a.txt", "a");
        workspace.File("b.txt", "b");

        var state = new ChatTuiState(workspace.Path);
        state.Insert('@');

        MentionPicker.MoveSelection(state, -1);
        Assert.Equal("b.txt", MentionPicker.Selected(state));
        MentionPicker.MoveSelection(state, 1);
        Assert.Equal("a.txt", MentionPicker.Selected(state));
    }

    [Fact]
    public void KeysMapToMentionActions()
    {
        using var workspace = new TempWorkspace();
        workspace.File("a.txt", "a");
        var state = new ChatTuiState(workspace.Path);
        state.Insert('@');

        Assert.Equal(ChatKeyAction.MentionPickerUp,
            ChatKeyMapper.Map(Key(ConsoleKey.UpArrow), state));
        Assert.Equal(ChatKeyAction.MentionPickerDown,
            ChatKeyMapper.Map(Key(ConsoleKey.DownArrow), state));
        Assert.Equal(ChatKeyAction.MentionPickerPreviousFilter,
            ChatKeyMapper.Map(Key(ConsoleKey.LeftArrow), state));
        Assert.Equal(ChatKeyAction.MentionPickerNextFilter,
            ChatKeyMapper.Map(Key(ConsoleKey.RightArrow), state));
        Assert.Equal(ChatKeyAction.MentionPickerAccept,
            ChatKeyMapper.Map(Key(ConsoleKey.Enter), state));
        Assert.Equal(ChatKeyAction.MentionPickerAccept,
            ChatKeyMapper.Map(Key(ConsoleKey.Tab), state));
        Assert.Equal(ChatKeyAction.MentionPickerClose,
            ChatKeyMapper.Map(Key(ConsoleKey.Escape), state));
    }

    [Fact]
    public void OverlayKeepsTranscriptAndScroll()
    {
        using var workspace = new TempWorkspace();
        workspace.File("a.txt", "a");
        var state = new ChatTuiState(workspace.Path);
        state.AddInfo("seed");
        state.TranscriptScroll = 1;

        var before = ChatTuiRenderer.TranscriptLines(state, 80);
        state.Insert('@');

        Assert.Equal(before, ChatTuiRenderer.TranscriptLines(state, 80));
        Assert.Equal(1, state.TranscriptScroll);
    }

    [Fact]
    public void FiltersCycleAndResetSelection()
    {
        using var workspace = new TempWorkspace();
        workspace.File("a.txt", "a");
        workspace.File("b.txt", "b");
        var state = new ChatTuiState(workspace.Path);
        state.Insert('@');
        MentionPicker.MoveSelection(state, 1);

        MentionPicker.MoveFilter(state, 1);
        Assert.Equal(MentionFilter.FilesystemOnly, state.MentionPicker.Filter);
        Assert.Equal(0, state.MentionPicker.Selection);

        MentionPicker.MoveFilter(state, 1);
        Assert.Equal(MentionFilter.AllResults, state.MentionPicker.Filter);
    }
    private static ConsoleKeyInfo Key(ConsoleKey key) =>
        new('\0', key, shift: false, alt: false, control: false);
    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kl-mention-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void File(string relative, string content)
        {
            var path = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            System.IO.File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Best effort in tests.
            }
        }
    }
}

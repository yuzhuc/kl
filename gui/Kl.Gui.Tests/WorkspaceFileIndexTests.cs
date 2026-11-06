using Kl.Gui.Services;

namespace Kl.Gui.Tests;

public sealed class WorkspaceFileIndexTests : IDisposable
{
    private readonly string _root;

    public WorkspaceFileIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "KlFileIndexTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "deep"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        File.WriteAllText(Path.Combine(_root, "README.md"), "alpha\nneedle here\nomega\n");
        File.WriteAllText(Path.Combine(_root, "src", "App.cs"), "line one\nNEEDLE line two\nthree\n");
        File.WriteAllText(Path.Combine(_root, "src", "deep", "Tool.cs"), "deep needle\n");
        File.WriteAllText(Path.Combine(_root, "node_modules", "skip.js"), "needle\n");
        var binary = new byte[] { (byte)'a', 0, (byte)'b' };
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), binary);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void FilesSkipsNoiseDirectoriesAndUsesForwardSlashes()
    {
        var files = WorkspaceFileIndex.Files(_root);

        Assert.Contains("src/App.cs", files);
        Assert.Contains("src/deep/Tool.cs", files);
        Assert.DoesNotContain("node_modules/skip.js", files);
        Assert.All(files, file => Assert.DoesNotContain('\\', file));
    }

    [Fact]
    public void MatchPathsRanksFileNamePrefixesFirst()
    {
        var files = new[] { "docs/app-notes.md", "src/App.cs", "src/helper.cs" };

        var matches = WorkspaceFileIndex.MatchPaths(files, "app", 10);

        Assert.Equal("src/App.cs", matches[0]);
        Assert.Contains("docs/app-notes.md", matches);
        Assert.DoesNotContain("src/helper.cs", matches);
    }

    [Fact]
    public void MatchPathsIgnoresEmptyQueries()
    {
        Assert.Empty(WorkspaceFileIndex.MatchPaths(new[] { "a.cs" }, "  ", 10));
    }

    [Fact]
    public void SearchContentsGroupsMatchesByFileWithLineNumbers()
    {
        var files = WorkspaceFileIndex.Files(_root);

        var groups = WorkspaceFileIndex.SearchContents(_root, files, "needle", 10, 5);

        var readme = Assert.Single(groups, group => group.RelativePath == "README.md");
        var line = Assert.Single(readme.Lines);
        Assert.Equal(2, line.LineNumber);
        Assert.Equal("needle here", line.Text);

        var app = Assert.Single(groups, group => group.RelativePath == "src/App.cs");
        Assert.Equal(2, Assert.Single(app.Lines).LineNumber);
        Assert.DoesNotContain(groups, group => group.RelativePath == "node_modules/skip.js");
    }

    [Fact]
    public void SearchContentsSkipsBinaryFiles()
    {
        var files = WorkspaceFileIndex.Files(_root);

        var groups = WorkspaceFileIndex.SearchContents(_root, files, "a", 20, 5);

        Assert.DoesNotContain(groups, group => group.RelativePath == "blob.bin");
    }

    [Fact]
    public void SearchContentsRespectsGroupLimit()
    {
        var files = WorkspaceFileIndex.Files(_root);

        var groups = WorkspaceFileIndex.SearchContents(_root, files, "needle", 1, 5);

        Assert.Single(groups);
    }

    [Fact]
    public void InvalidateDropsTheCachedListing()
    {
        WorkspaceFileIndex.Files(_root);
        File.WriteAllText(Path.Combine(_root, "src", "New.cs"), "x\n");

        Assert.DoesNotContain("src/New.cs", WorkspaceFileIndex.Files(_root));

        WorkspaceFileIndex.Invalidate(_root);

        Assert.Contains("src/New.cs", WorkspaceFileIndex.Files(_root));
    }
}

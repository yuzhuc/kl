using Kl.Gui.Services;

namespace Kl.Gui.Tests;

/// <summary>Rules behind the composer "/" and "@" suggestion menus.</summary>
public sealed class ComposerSuggestionRulesTests
{
    [Fact]
    public void SlashTokenIsDetectedAtTheStart()
    {
        Assert.True(ComposerSuggestionRules.TryGetToken("/compact", 8, out var token));
        Assert.Equal('/', token.Trigger);
        Assert.Equal("compact", token.Query);
        Assert.Equal(0, token.Start);
    }

    [Fact]
    public void SlashTokenIsDetectedAfterWhitespace()
    {
        Assert.True(ComposerSuggestionRules.TryGetToken("hello /re", 9, out var token));
        Assert.Equal('/', token.Trigger);
        Assert.Equal("re", token.Query);
        Assert.Equal(6, token.Start);
    }

    [Fact]
    public void AtInsideAWordDoesNotOpenTheFileMenu()
    {
        Assert.False(ComposerSuggestionRules.TryGetToken("a@b", 3, out _));
    }

    [Fact]
    public void AtTokenKeepsItsQuery()
    {
        Assert.True(ComposerSuggestionRules.TryGetToken("look at @src/Pro", 17, out var token));
        Assert.Equal('@', token.Trigger);
        Assert.Equal("src/Pro", token.Query);
        Assert.Equal(8, token.Start);
    }

    [Fact]
    public void BareAtTokenHasAnEmptyQuery()
    {
        Assert.True(ComposerSuggestionRules.TryGetToken("@", 1, out var token));
        Assert.Equal(string.Empty, token.Query);
    }

    [Fact]
    public void WhitespaceAfterTheTokenClosesTheMenu()
    {
        Assert.False(ComposerSuggestionRules.TryGetToken("/compact ", 9, out _));
    }

    [Fact]
    public void FilterRanksPrefixMatchesFirst()
    {
        var files = new[] { "src/Program.cs", "docs/program.md", "README.md", "src/App.axaml" };
        var filtered = ComposerSuggestionRules.Filter(files, "pro", 10);

        Assert.Equal("src/Program.cs", filtered[0]);
        Assert.Contains("docs/program.md", filtered);
        Assert.DoesNotContain("README.md", filtered);
    }

    [Fact]
    public void FilterHonoursTheLimit()
    {
        var files = Enumerable.Range(0, 40).Select(index => $"file{index:00}.cs").ToList();
        Assert.Equal(12, ComposerSuggestionRules.Filter(files, string.Empty, 12).Count);
    }

    [Fact]
    public void ReplaceTokenKeepsSurroundingText()
    {
        var text = "look at @src/Pro now";
        Assert.True(ComposerSuggestionRules.TryGetToken(text, 16, out var token));
        var (result, caret) = ComposerSuggestionRules.ReplaceToken(text, 16, token, "@src/Program.cs ");

        Assert.Equal("look at @src/Program.cs now", result);
        Assert.Equal(24, caret);
    }

    [Fact]
    public void SkippedDirectoriesCoverTheUsualNoise()
    {
        Assert.Contains("node_modules", ComposerSuggestionRules.SkippedDirectories);
        Assert.Contains("target", ComposerSuggestionRules.SkippedDirectories);
        Assert.DoesNotContain("src", ComposerSuggestionRules.SkippedDirectories);
    }
}

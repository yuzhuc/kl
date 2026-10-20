using Xunit;

namespace Kl.Cli.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void DefaultsToChat()
    {
        var options = CommandLine.Parse([]);
        Assert.Equal("chat", options.Command);
        Assert.Empty(options.Positionals);
    }

    [Fact]
    public void ParsesRunTaskAndApproval()
    {
        var options = CommandLine.Parse(["run", "fix", "the", "test", "--yes"]);
        Assert.Equal("run", options.Command);
        Assert.Equal("fix the test", options.TaskText);
        Assert.True(options.AutoApprove);
    }

    [Fact]
    public void ParsesNestedProviderCommand()
    {
        var options = CommandLine.Parse(["provider", "list", "--provider", "anthropic"]);
        Assert.Equal("provider", options.Command);
        Assert.Equal(["list"], options.Positionals);
        Assert.Equal("anthropic", options.Provider);
    }

    [Fact]
    public void ParsesPluginSubcommand()
    {
        var options = CommandLine.Parse(["plugin", "install", "plugins/examples/fake-tool"]);
        Assert.Equal("plugin", options.Command);
        Assert.Equal(["install", "plugins/examples/fake-tool"], options.Positionals);
    }

    [Fact]
    public void ParsesProviderAdd()
    {
        var options = CommandLine.Parse(["provider", "add"]);
        Assert.Equal("provider", options.Command);
        Assert.Equal(["add"], options.Positionals);
    }

    [Fact]
    public void ParsesAuthSet()
    {
        var options = CommandLine.Parse(["auth", "set", "opencode"]);
        Assert.Equal("auth", options.Command);
        Assert.Equal(["set", "opencode"], options.Positionals);
    }

    [Fact]
    public void ParsesModelSelect()
    {
        var options = CommandLine.Parse([
            "model", "select", "--provider", "opencode", "--model", "gpt-5-responses"
        ]);
        Assert.Equal("model", options.Command);
        Assert.Equal(["select"], options.Positionals);
        Assert.Equal("opencode", options.Provider);
        Assert.Equal("gpt-5-responses", options.Model);
    }
    [Fact]
    public void ParsesReviewOptions()
    {
        var options = CommandLine.Parse([
            "review", "--base", "HEAD~1", "--strict", "--mode", "strict", "--quality", "deep"
        ]);
        Assert.Equal("review", options.Command);
        Assert.Equal("HEAD~1", options.BaseRef);
        Assert.True(options.Strict);
        Assert.Equal("strict", options.ReviewMode);
        Assert.Equal("deep", options.Quality);
    }

    [Fact]
    public void ParsesQualityOption()
    {
        var options = CommandLine.Parse(["run", "fix", "it", "--quality", "fast"]);
        Assert.Equal("fast", options.Quality);
    }

    [Fact]
    public void ParsesAgentPreset()
    {
        var options = CommandLine.Parse(["run", "inspect", "--agent", "plan"]);
        Assert.Equal("plan", options.Agent);
        Assert.Equal("plan", options.ToSessionOptions().Agent);
    }

    [Fact]
    public void AcceptsCustomAgentPreset()
    {
        var options = CommandLine.Parse(["run", "task", "--agent", "custom-reviewer"]);
        Assert.Equal("custom-reviewer", options.Agent);
    }

    [Fact]
    public void RejectsUnknownReviewMode()
    {
        Assert.Throws<ArgumentException>(() =>
            CommandLine.Parse(["review", "--mode", "turbo"]));
    }

    [Fact]
    public void RejectsUnknownQualityMode()
    {
        Assert.Throws<ArgumentException>(() =>
            CommandLine.Parse(["run", "task", "--quality", "ultra"]));
    }

    [Fact]
    public void RejectsUnknownOption()
    {
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(["doctor", "--bogus"]));
    }
}

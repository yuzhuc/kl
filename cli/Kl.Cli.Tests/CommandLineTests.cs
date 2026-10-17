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
    public void RejectsUnknownOption()
    {
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(["doctor", "--bogus"]));
    }
}

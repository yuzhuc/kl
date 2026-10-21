using Xunit;

namespace Kl.Tui.Tests;

public sealed class MemoryCommandTests
{
    [Fact]
    public void MemoryIsRegisteredInThePalette()
    {
        var command = ChatCommandPalette.FindExact("/memory");
        Assert.NotNull(command);
        Assert.Equal(ChatCommandKind.Memory, command!.Kind);
    }

    [Fact]
    public void BareMemoryLists()
    {
        Assert.True(MemoryCommandParser.TryParse("/memory", out var command));
        Assert.Equal(MemoryCommandAction.List, command.Action);
        Assert.Equal(string.Empty, command.Name);
        Assert.Null(command.Scope);
    }

    [Fact]
    public void ListReadWriteAndDeleteAreParsed()
    {
        Assert.True(MemoryCommandParser.TryParse("/memory list", out var list));
        Assert.Equal(MemoryCommandAction.List, list.Action);

        Assert.True(MemoryCommandParser.TryParse("/memory read notes.md", out var read));
        Assert.Equal(MemoryCommandAction.Read, read.Action);
        Assert.Equal("notes.md", read.Name);
        Assert.Equal(string.Empty, read.Content);

        Assert.True(MemoryCommandParser.TryParse("/memory write notes.md hello world", out var write));
        Assert.Equal(MemoryCommandAction.Write, write.Action);
        Assert.Equal("notes.md", write.Name);
        Assert.Equal("hello world", write.Content);

        Assert.True(MemoryCommandParser.TryParse("/memory delete notes.md", out var delete));
        Assert.Equal(MemoryCommandAction.Delete, delete.Action);
        Assert.Equal("notes.md", delete.Name);
    }

    [Fact]
    public void UserScopePrefixSelectsTheUserStore()
    {
        Assert.True(MemoryCommandParser.TryParse("/memory read user:notes.md", out var read));
        Assert.Equal("user", read.Scope);
        Assert.Equal("notes.md", read.Name);

        Assert.True(MemoryCommandParser.TryParse("/memory write user:notes.md hi", out var write));
        Assert.Equal("user", write.Scope);
        Assert.Equal("hi", write.Content);
    }

    [Fact]
    public void UnknownActionsAndOtherCommandsAreRejected()
    {
        Assert.False(MemoryCommandParser.TryParse("/memory bogus", out _));
        Assert.False(MemoryCommandParser.TryParse("/memoryx", out _));
        Assert.False(MemoryCommandParser.TryParse("/cost", out _));
        Assert.False(MemoryCommandParser.TryParse(null, out _));
        Assert.False(MemoryCommandParser.TryParse("   ", out _));
    }
}

using Kl.Gui.Services;

namespace Kl.Gui.Tests;

public sealed class AttachmentPromptTextTests
{
    [Fact]
    public void PlainTextIsUntouched()
    {
        Assert.Equal("hello", AttachmentPromptText.Collapse("hello"));
        Assert.Equal(string.Empty, AttachmentPromptText.Collapse(string.Empty));
    }

    [Fact]
    public void InlineAttachmentBlocksCollapseToTheirNames()
    {
        var text = "fix this\n\n<attachment name=\"a.txt\" path=\"D:/a.txt\">\ncontents\n</attachment>"
            + "\n\n<attachment name=\"b.cs\" path=\"D:/b.cs\">\nmore\n</attachment>";

        Assert.Equal("fix this\n\n附件：a.txt、b.cs", AttachmentPromptText.Collapse(text));
    }

    [Fact]
    public void AttachmentOnlyPromptKeepsTheSummary()
    {
        var text = "<attachment name=\"notes.md\" path=\"D:/notes.md\">\nbody\n</attachment>";
        Assert.Equal("附件：notes.md", AttachmentPromptText.Collapse(text));
    }

    [Fact]
    public void UnclosedBlockIsLeftAlone()
    {
        var text = "<attachment name=\"a.txt\" path=\"D:/a.txt\">\nbody";
        Assert.Equal(text, AttachmentPromptText.Collapse(text));
    }
}

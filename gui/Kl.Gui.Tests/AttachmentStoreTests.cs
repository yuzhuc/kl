using Kl.Gui.Services;

namespace Kl.Gui.Tests;

public sealed class AttachmentStoreTests
{
    [Fact]
    public void SaveWritesEachAttachmentUnderItsSession()
    {
        using var temp = new TempDirectory();
        var directory = AttachmentStore.Save(
            temp.Path,
            "sess-1",
            [("a.txt", "one"), ("bad/name?.md", "two")]);

        Assert.Equal(Path.Combine(temp.Path, "attachments", "sess-1"), directory);
        Assert.Equal("one", File.ReadAllText(Path.Combine(directory, "1-a.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(directory, "2-bad_name_.md")));
    }

    [Fact]
    public void SafeFileNameFallsBackForEmptyNames()
    {
        Assert.Equal("attachment.txt", AttachmentStore.SafeFileName("   "));
        Assert.Equal(new string('a', 80), AttachmentStore.SafeFileName(new string('a', 200)));
    }
}

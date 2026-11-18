using Kl.Gui.Services;

namespace Kl.Gui.Tests;

public sealed class AppLogTests
{
    [Fact]
    public void WritesTimestampedLinesIntoTheLogFolder()
    {
        using var temp = new TempDirectory();

        AppLog.Write(temp.Path, "first failure");
        AppLog.Write(temp.Path, "second failure");

        var path = AppLog.LogFilePath(temp.Path);
        Assert.True(File.Exists(path));
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("first failure", lines[0]);
        Assert.Contains("second failure", lines[1]);
    }
}

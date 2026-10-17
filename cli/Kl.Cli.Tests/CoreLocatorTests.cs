using Xunit;

namespace Kl.Cli.Tests;

public sealed class CoreLocatorTests
{
    [Fact]
    public void AcceptsExplicitDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kl-core-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, CoreLocator.ExecutableName);
            File.WriteAllText(executable, string.Empty);
            Assert.Equal(executable, CoreLocator.Find(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsMissingExplicitPath()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), CoreLocator.ExecutableName);
        Assert.Throws<FileNotFoundException>(() => CoreLocator.Find(path));
    }
}

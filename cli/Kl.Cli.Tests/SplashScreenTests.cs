using Xunit;

namespace Kl.Cli.Tests;

public sealed class SplashScreenTests
{
    [Fact]
    public void StartupShowsLargeKlProgressAndVersion()
    {
        var lines = RenderLines(80, 24, "Opening session");

        Assert.Equal(24, lines.Length);
        Assert.Contains(lines, line => line.Contains("██╗  ██╗██╗"));
        Assert.Equal("› Opening session", lines[21].Trim());
        Assert.Equal(SplashScreen.VersionText, lines[23].Trim());
        Assert.DoesNotContain(">_ KL", string.Join('\n', lines));
        Assert.DoesNotContain("Ask KL", string.Join('\n', lines));
    }

    [Fact]
    public void NarrowTerminalStillShowsKlProgressAndVersion()
    {
        var lines = RenderLines(30, 10, "Connecting to core");

        Assert.Equal(10, lines.Length);
        Assert.Contains(lines, line => line.Trim() == "KL");
        Assert.Equal("› Connecting to core", lines[7].Trim());
        Assert.Equal(SplashScreen.VersionText, lines[9].Trim());
    }

    [Fact]
    public void FrameUsesCodexDimPaletteAndResets()
    {
        var frame = SplashScreen.RenderFrame(80, 24, "Ready");

        Assert.Contains("\u001b[2J\u001b[H", frame);
        Assert.Contains("\u001b[38;5;244m", frame);
        Assert.EndsWith(DiffRender.Ansi.Reset, frame);
    }

    [Fact]
    public async Task NonTuiDoesNotWriteAnything()
    {
        var writes = new List<string>();

        await SplashScreen.ShowAsync(
            canUseTui: false,
            getWidth: () => throw new InvalidOperationException("width should not be read"),
            getHeight: () => throw new InvalidOperationException("height should not be read"),
            write: writes.Add);

        Assert.Empty(writes);
    }

    [Fact]
    public async Task TuiRendersOneLoadingFrame()
    {
        var writes = new List<string>();

        await SplashScreen.ShowAsync(
            canUseTui: true,
            getWidth: () => 80,
            getHeight: () => 24,
            write: writes.Add);

        var frame = Assert.Single(writes);
        Assert.Contains("› loading", DiffRender.Ansi.Strip(frame));
    }

    private static string[] RenderLines(int width, int height, string status) =>
        DiffRender.Ansi
            .Strip(SplashScreen.RenderFrame(width, height, status))
            .Split('\n');
}

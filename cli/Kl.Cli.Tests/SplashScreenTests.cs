using Xunit;

namespace Kl.Cli.Tests;

public sealed class SplashScreenTests
{
    [Fact]
    public void LogoIsCenteredIn80By24()
    {
        var lines = RenderLines(80, 24, bright: false);

        Assert.Equal(24, lines.Length);
        Assert.All(lines[..8], line => Assert.Equal(string.Empty, line));

        for (var index = 8; index < 14; index++)
        {
            Assert.Equal(32, lines[index].Length - lines[index].TrimStart().Length);
            Assert.Equal(48, DisplayText.Width(lines[index]));
        }

        Assert.Equal(string.Empty, lines[14]);
        Assert.Equal(36, lines[15].Length - lines[15].TrimStart().Length);
        Assert.Equal("kl 0.1.0", lines[15].Trim());
        Assert.All(lines[16..], line => Assert.Equal(string.Empty, line));
    }

    [Fact]
    public void NarrowTerminalFallsBackToSingleLine()
    {
        var lines = RenderLines(30, 10, bright: false);

        Assert.Equal(10, lines.Length);
        Assert.Equal(">_ KL", lines[4].Trim());
        Assert.Equal(12, lines[4].Length - lines[4].TrimStart().Length);
        Assert.Equal("kl 0.1.0", lines[5].Trim());
        Assert.DoesNotContain("██", string.Join('\n', lines));
    }

    [Fact]
    public void BothFramesContainLogoAndVersion()
    {
        foreach (var bright in new[] { false, true })
        {
            var frame = SplashScreen.RenderFrame(80, 24, bright);
            var plain = DiffRender.Ansi.Strip(frame);

            Assert.Contains("\u001b[2J\u001b[H", frame);
            Assert.Contains("██╗  ██╗██╗", plain);
            Assert.Contains("╚═╝  ╚═╝╚══════╝", plain);
            Assert.Contains("kl 0.1.0", plain);
        }

        Assert.Contains("\u001b[38;5;244m", SplashScreen.RenderFrame(80, 24, bright: false));
        Assert.Contains("\u001b[1;97m", SplashScreen.RenderFrame(80, 24, bright: true));
    }

    [Fact]
    public async Task NonTuiDoesNotWriteAnything()
    {
        var writes = new List<string>();

        await SplashScreen.ShowAsync(
            canUseTui: false,
            getWidth: () => throw new InvalidOperationException("width should not be read"),
            getHeight: () => throw new InvalidOperationException("height should not be read"),
            keyPressed: () => throw new InvalidOperationException("keys should not be read"),
            write: writes.Add,
            delay: (_, _) => throw new InvalidOperationException("delay should not run"));

        Assert.Empty(writes);
    }

    [Fact]
    public async Task AnyKeySkipsRemainingFrames()
    {
        var writes = 0;
        var delays = 0;

        await SplashScreen.ShowAsync(
            canUseTui: true,
            getWidth: () => 80,
            getHeight: () => 24,
            keyPressed: () => true,
            write: _ => { writes++; },
            delay: (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            });

        Assert.Equal(2, writes);
        Assert.Equal(0, delays);
    }

    [Fact]
    public async Task FrameTimingUsesInjectedDelayAndStaysUnder300Ms()
    {
        var writes = new List<string>();
        var elapsed = TimeSpan.Zero;

        await SplashScreen.ShowAsync(
            canUseTui: true,
            getWidth: () => 80,
            getHeight: () => 24,
            keyPressed: () => false,
            write: writes.Add,
            delay: (delay, _) =>
            {
                elapsed += delay;
                return Task.CompletedTask;
            });

        Assert.Equal(2, writes.Count(write => write.Contains("kl 0.1.0")));
        Assert.Equal(TimeSpan.FromMilliseconds(240), elapsed);
        Assert.True(elapsed <= TimeSpan.FromMilliseconds(300));
    }

    private static string[] RenderLines(int width, int height, bool bright) =>
        DiffRender.Ansi
            .Strip(SplashScreen.RenderFrame(width, height, bright))
            .Split('\n');
}
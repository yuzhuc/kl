namespace Kl.Cli;

/// <summary>Two-frame startup splash shown after the TUI enters the alternate screen.</summary>
public static class SplashScreen
{
    public const int MinimumWidth = 32;
    public static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(120);

    private const string DarkColor = "\u001b[38;5;244m";
    private const string BrightColor = "\u001b[1;97m";

    private static readonly string[] LogoLines =
    [
        "██╗  ██╗██╗",
        "██║ ██╔╝██║",
        "█████╔╝ ██║",
        "██╔═██╗ ██║",
        "██║  ██║███████╗",
        "╚═╝  ╚═╝╚══════╝"
    ];

    public static string VersionLine
    {
        get
        {
            var version = typeof(SplashScreen).Assembly.GetName().Version;
            return version is null ? "kl 0.1.0" : $"kl {version.ToString(3)}";
        }
    }

    public static Task ShowAsync(
        bool canUseTui,
        CancellationToken cancellationToken = default) =>
        ShowAsync(
            canUseTui,
            () => Console.WindowWidth,
            () => Console.WindowHeight,
            TryConsumeKey,
            text =>
            {
                Console.Out.Write(text);
                Console.Out.Flush();
            },
            (delay, token) => Task.Delay(delay, token),
            cancellationToken);

    public static async Task ShowAsync(
        bool canUseTui,
        Func<int> getWidth,
        Func<int> getHeight,
        Func<bool> keyPressed,
        Action<string> write,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken = default)
    {
        if (!canUseTui)
        {
            return;
        }

        try
        {
            var width = getWidth();
            var height = getHeight();
            write(RenderFrame(width, height, bright: false));
            if (await WaitFrameAsync(keyPressed, delay, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            write(RenderFrame(width, height, bright: true));
            await WaitFrameAsync(keyPressed, delay, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            write(DiffRender.Ansi.Reset);
        }
    }

    public static string RenderFrame(int width, int height, bool bright)
    {
        var body = RenderLayout(width, height);
        var color = bright ? BrightColor : DarkColor;
        return "\u001b[2J\u001b[H" + color + body + DiffRender.Ansi.Reset;
    }

    private static async Task<bool> WaitFrameAsync(
        Func<bool> keyPressed,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        if (keyPressed())
        {
            return true;
        }

        await delay(FrameDuration, cancellationToken).ConfigureAwait(false);
        return keyPressed();
    }

    private static string RenderLayout(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var lines = new List<string>();
        if (width >= MinimumWidth)
        {
            var logoWidth = LogoLines.Max(DisplayText.Width);
            var left = Math.Max(0, (width - logoWidth) / 2);
            foreach (var logoLine in LogoLines)
            {
                var padding = new string(' ', logoWidth - DisplayText.Width(logoLine));
                lines.Add(new string(' ', left) + logoLine + padding);
            }
            lines.Add(string.Empty);
        }
        else
        {
            lines.Add(Center(">_ KL", width));
        }

        lines.Add(Center(VersionLine, width));

        var top = Math.Max(0, (height - lines.Count) / 2);
        var output = new List<string>(height);
        output.AddRange(Enumerable.Repeat(string.Empty, Math.Min(top, height)));
        foreach (var line in lines)
        {
            if (output.Count >= height)
            {
                break;
            }
            output.Add(line);
        }
        while (output.Count < height)
        {
            output.Add(string.Empty);
        }
        return string.Join('\n', output);
    }

    private static string Center(string text, int width)
    {
        var left = Math.Max(0, (width - DisplayText.Width(text)) / 2);
        return new string(' ', left) + text;
    }

    private static bool TryConsumeKey()
    {
        try
        {
            if (!Console.KeyAvailable)
            {
                return false;
            }
            Console.ReadKey(intercept: true);
            return true;
        }
        catch (Exception error)
            when (error is IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
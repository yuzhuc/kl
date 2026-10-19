namespace Kl.Cli;

/// <summary>Codex-style startup screen updated while the session opens.</summary>
public static class SplashScreen
{
    public const int MinimumWidth = 64;

    private const string DimColor = "\u001b[38;5;244m";

    private static readonly IReadOnlyList<string> LogoLines = KlBrand.LogoLines;
    private static bool _started;
    private static int _lastWidth;
    private static int _lastHeight;
    private static string? _lastStatus;

    public static string VersionText
    {
        get
        {
            var version = typeof(KlBrand).Assembly.GetName().Version;
            return version is null ? "v0.1.0" : $"v{version.ToString(3)}";
        }
    }

    public static void Start(bool canUseTui, string status)
    {
        if (!canUseTui)
        {
            return;
        }
        WriteFullFrame(status);
    }

    public static void Update(bool canUseTui, string status)
    {
        if (!canUseTui || status == _lastStatus)
        {
            return;
        }

        var width = Math.Max(1, Console.WindowWidth);
        var height = Math.Max(1, Console.WindowHeight);
        if (!_started || width != _lastWidth || height != _lastHeight)
        {
            WriteFullFrame(status);
            return;
        }

        Console.Out.Write(
            $"\u001b[{Math.Max(1, height - 2)};1H\u001b[2K" +
            RenderStatusLine(width, status));
        Console.Out.Flush();
        _lastStatus = status;
    }

    public static Task ShowAsync(
        bool canUseTui,
        CancellationToken cancellationToken = default) =>
        ShowAsync(
            canUseTui,
            () => Console.WindowWidth,
            () => Console.WindowHeight,
            text =>
            {
                Console.Out.Write(text);
                Console.Out.Flush();
            },
            cancellationToken);

    public static Task ShowAsync(
        bool canUseTui,
        Func<int> getWidth,
        Func<int> getHeight,
        Action<string> write,
        CancellationToken cancellationToken = default)
    {
        if (!canUseTui)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        write(RenderFrame(getWidth(), getHeight(), "loading"));
        return Task.CompletedTask;
    }

    public static string RenderFrame(int width, int height) =>
        RenderFrame(width, height, "loading");

    public static string RenderFrame(int width, int height, string status) =>
        "\u001b[2J\u001b[H" + RenderLayout(width, height, status) + DiffRender.Ansi.Reset;

    private static void WriteFullFrame(string status)
    {
        _lastWidth = Math.Max(1, Console.WindowWidth);
        _lastHeight = Math.Max(1, Console.WindowHeight);
        _lastStatus = status;
        Console.Out.Write(RenderFrame(_lastWidth, _lastHeight, status));
        Console.Out.Flush();
        _started = true;
    }

    private static string RenderLayout(int width, int height, string status)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var lines = Enumerable.Repeat(string.Empty, height).ToArray();

        var logo = width >= MinimumWidth ? LogoLines : new[] { "KL" };
        var logoWidth = logo.Max(DisplayText.Width);
        var left = Math.Max(0, (width - logoWidth) / 2);
        var top = Math.Max(0, (height - logo.Count - 2) / 2);
        for (var index = 0; index < logo.Count && top + index < height - 3; index++)
        {
            var line = logo[index];
            var padding = new string(' ', Math.Max(0, logoWidth - DisplayText.Width(line)));
            lines[top + index] = new string(' ', left) +
                DimColor + line + padding + DiffRender.Ansi.Reset;
        }

        var logRow = Math.Max(0, height - 3);
        lines[logRow] = RenderStatusLine(width, status);
        if (height > 1)
        {
            var versionLeft = Math.Max(0, width - DisplayText.Width(VersionText) - 2);
            lines[height - 1] = new string(' ', versionLeft) +
                DimColor + VersionText + DiffRender.Ansi.Reset;
        }

        return string.Join('\n', lines.Select(line => DiffRender.Ansi.Fit(line, width)));
    }

    private static string RenderStatusLine(int width, string status) =>
        DiffRender.Ansi.Fit(
            "  " + DimColor + "› " + status + DiffRender.Ansi.Reset,
            width);
}

namespace Kl.Cli;

public static class KlBrand
{
    public const string InputPlaceholder = "Ask KL to do anything";
    public const string ShortcutsHint = "? for shortcuts";

    public static readonly IReadOnlyList<string> LogoLines =
    [
        "██╗  ██╗██╗",
        "██║ ██╔╝██║",
        "█████╔╝ ██║",
        "██╔═██╗ ██║",
        "██║  ██║███████╗",
        "╚═╝  ╚═╝╚══════╝"
    ];

    public static string TitleLine
    {
        get
        {
            var version = typeof(KlBrand).Assembly.GetName().Version;
            return version is null ? ">_ KL" : $">_ KL (v{version.ToString(3)})";
        }
    }
}
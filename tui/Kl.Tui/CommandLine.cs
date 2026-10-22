namespace Kl.Tui;

public sealed class TuiOptions
{
    public string Command { get; set; } = "chat";
    public List<string> Positionals { get; } = [];
    public string? CorePath { get; set; }
    public string? ConfigPath { get; set; }
    public string? HomePath { get; set; }
    public string? Workspace { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? Session { get; set; }
    public string? SocketName { get; set; }
    public string? BaseRef { get; set; }
    public bool Strict { get; set; }
    public string? ReviewMode { get; set; }
    public string? Quality { get; set; }
    public string? Agent { get; set; }
    public bool AutoApprove { get; set; }
    public bool Verbose { get; set; }
    public bool Help { get; set; }
    public bool Version { get; set; }

    public string TaskText => string.Join(' ', Positionals).Trim();

    public KlClientOptions ToClientOptions() => new()
    {
        CorePath = CorePath,
        ConfigPath = ConfigPath,
        HomePath = HomePath
    };

    public KlSessionOptions ToSessionOptions() => new()
    {
        SessionId = Session,
        Workspace = Workspace,
        Provider = Provider,
        Model = Model,
        Quality = Quality,
        Agent = Agent,
        Verbose = Verbose
    };
}

public static class CommandLine
{
    private static readonly HashSet<string> Commands =
    [
        "chat", "run", "resume", "doctor", "ping", "provider", "auth", "model", "plugin", "review", "audit", "config", "serve", "version", "help"
    ];

    public static TuiOptions Parse(string[] args)
    {
        var options = new TuiOptions();
        var commandSet = false;
        if (args.Length == 0)
        {
            return options;
        }

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--core":
                    options.CorePath = Next(args, ref i, arg);
                    break;
                case "--config":
                    options.ConfigPath = Next(args, ref i, arg);
                    break;
                case "--home":
                    options.HomePath = Next(args, ref i, arg);
                    break;
                case "--workspace":
                case "-C":
                    options.Workspace = Next(args, ref i, arg);
                    break;
                case "--provider":
                    options.Provider = Next(args, ref i, arg);
                    break;
                case "--model":
                    options.Model = Next(args, ref i, arg);
                    break;
                case "--session":
                    options.Session = Next(args, ref i, arg);
                    break;
                case "--socket":
                    options.SocketName = Next(args, ref i, arg);
                    break;
                case "--base":
                    options.BaseRef = Next(args, ref i, arg);
                    break;
                case "--strict":
                    options.Strict = true;
                    break;
                case "--mode":
                    options.ReviewMode = Next(args, ref i, arg);
                    if (options.ReviewMode is not ("quick" or "standard" or "strict"))
                    {
                        throw new ArgumentException($"unknown review mode: {options.ReviewMode}");
                    }
                    break;
                case "--quality":
                    options.Quality = Next(args, ref i, arg);
                    if (options.Quality is not ("fast" or "balanced" or "deep"))
                    {
                        throw new ArgumentException($"unknown quality mode: {options.Quality}");
                    }
                    break;
                case "--agent":
                    options.Agent = Next(args, ref i, arg);
                    if (string.IsNullOrWhiteSpace(options.Agent))
                    {
                        throw new ArgumentException("--agent requires an id");
                    }
                    break;
                case "--yes":
                case "-y":
                    options.AutoApprove = true;
                    break;
                case "--verbose":
                case "-v":
                    options.Verbose = true;
                    break;
                case "--help":
                case "-h":
                    options.Help = true;
                    break;
                case "--version":
                case "-V":
                    options.Version = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        throw new ArgumentException($"unknown option: {arg}");
                    }
                    if (!commandSet && Commands.Contains(arg))
                    {
                        options.Command = arg;
                        commandSet = true;
                    }
                    else
                    {
                        options.Positionals.Add(arg);
                    }
                    break;
            }
        }

        if (options.Version)
        {
            options.Command = "version";
        }
        else if (options.Help)
        {
            options.Command = "help";
        }

        return options;
    }

    private static string Next(string[] args, ref int index, string flag)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"missing value for {flag}");
        }

        return args[++index];
    }
}

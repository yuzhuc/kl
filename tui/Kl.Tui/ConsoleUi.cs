namespace Kl.Tui;

public sealed class ConsoleUi
{
    private readonly bool _color;

    public ConsoleUi()
    {
        _color = !Console.IsOutputRedirected
            && Environment.GetEnvironmentVariable("NO_COLOR") is null;
    }

    public void Header(string text) => WriteLine(Bold(text));

    public void Line(string text = "") => WriteLine(text);

    public void Check(string name, bool ok, string detail)
    {
        var status = ok ? Paint("ok", "32") : Paint("fail", "31");
        WriteLine($"  [{status}] {name,-20} {detail}");
    }

    public void Tool(string name, string detail) =>
        WriteLine($"  {Paint("*", "36")} {name}: {detail}");

    public void Error(string text) => Console.Error.WriteLine(Paint(text, "31"));

    public void Delta(string text) => Console.Write(text);

    public void Diff(string diff)
    {
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                WriteLine(Paint(line, "32"));
            }
            else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
            {
                WriteLine(Paint(line, "31"));
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                WriteLine(Paint(line, "36"));
            }
            else
            {
                WriteLine(line);
            }
        }
    }

    private void WriteLine(string text)
    {
        Console.WriteLine(text);
    }

    private string Bold(string text) => Paint(text, "1");

    private string Paint(string text, string code) =>
        _color ? $"\u001b[{code}m{text}\u001b[0m" : text;
}

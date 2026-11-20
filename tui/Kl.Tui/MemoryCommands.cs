namespace Kl.Tui;

public enum MemoryCommandAction
{
    List,
    Read,
    Write,
    Delete
}

public sealed record MemoryCommand(
    MemoryCommandAction Action,
    string Name,
    string Content,
    string? Scope);

public static class MemoryCommandParser
{
    /// <summary>
    /// Parses `/memory [list|read|write|delete] [name] [content]`. The name
    /// may be prefixed with `user:` to target the user memory store.
    /// </summary>
    public static bool TryParse(string? input, out MemoryCommand command)
    {
        command = new MemoryCommand(MemoryCommandAction.List, string.Empty, string.Empty, null);
        var text = input?.Trim();
        if (text is null)
        {
            return false;
        }

        var separator = text.IndexOfAny([' ', '\t']);
        var head = separator < 0 ? text : text[..separator];
        if (!head.Equals("/memory", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = separator < 0 ? string.Empty : text[(separator + 1)..].Trim();
        if (rest.Length == 0)
        {
            command = new MemoryCommand(MemoryCommandAction.List, string.Empty, string.Empty, null);
            return true;
        }

        var actionEnd = rest.IndexOfAny([' ', '\t']);
        var actionText = actionEnd < 0 ? rest : rest[..actionEnd];
        var arguments = actionEnd < 0 ? string.Empty : rest[(actionEnd + 1)..].Trim();
        var action = actionText.ToLowerInvariant() switch
        {
            "list" => MemoryCommandAction.List,
            "read" => MemoryCommandAction.Read,
            "write" => MemoryCommandAction.Write,
            "delete" => MemoryCommandAction.Delete,
            _ => (MemoryCommandAction?)null
        };
        if (action is null)
        {
            return false;
        }

        string? scope = null;
        var name = arguments;
        if (action == MemoryCommandAction.List)
        {
            name = string.Empty;
        }
        else
        {
            var nameEnd = arguments.IndexOfAny([' ', '\t']);
            name = nameEnd < 0 ? arguments : arguments[..nameEnd];
            arguments = nameEnd < 0 ? string.Empty : arguments[(nameEnd + 1)..].Trim();
        }

        if (name.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
        {
            scope = "user";
            name = name["user:".Length..];
        }

        command = new MemoryCommand(
            action.Value,
            name,
            action == MemoryCommandAction.Write ? arguments : string.Empty,
            scope);
        return true;
    }
}

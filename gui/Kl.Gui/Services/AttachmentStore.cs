namespace Kl.Gui.Services;

/// <summary>Persists sent attachments next to the sessions they belong to.</summary>
public static class AttachmentStore
{
    /// <summary>Writes each attachment's text and returns the session directory.</summary>
    public static string Save(
        string homeDirectory,
        string sessionId,
        IEnumerable<(string Name, string Content)> attachments)
    {
        var directory = Path.Combine(homeDirectory, "attachments", sessionId);
        Directory.CreateDirectory(directory);
        var index = 0;
        foreach (var (name, content) in attachments)
        {
            index++;
            File.WriteAllText(Path.Combine(directory, $"{index}-{SafeFileName(name)}"), content);
        }
        return directory;
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();
        if (cleaned.Length == 0)
        {
            return "attachment.txt";
        }
        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }
}

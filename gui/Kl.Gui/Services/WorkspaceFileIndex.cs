namespace Kl.Gui.Services;

/// <summary>One matching line inside a file, used by the palette text results.</summary>
public sealed record PaletteLineMatch(int LineNumber, string Text);

/// <summary>
/// Bounded, cached workspace file listing plus name/content search for the
/// command palette. All methods are pure apart from the cache and are safe to
/// call from a background thread.
/// </summary>
public static class WorkspaceFileIndex
{
    private const int MaxFiles = 1500;
    private const int MaxDepth = 4;
    private const int MaxFileBytes = 512 * 1024;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, IReadOnlyList<string>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Relative paths with forward slashes, sorted; cached per workspace.</summary>
    public static IReadOnlyList<string> Files(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return Array.Empty<string>();
        }

        lock (CacheLock)
        {
            if (Cache.TryGetValue(workspace, out var cached))
            {
                return cached;
            }
        }

        var results = new List<string>();
        var pending = new Queue<(DirectoryInfo Directory, int Depth)>();
        pending.Enqueue((new DirectoryInfo(workspace), 0));
        while (pending.Count > 0 && results.Count < MaxFiles)
        {
            var (directory, depth) = pending.Dequeue();
            try
            {
                foreach (var file in directory.EnumerateFiles())
                {
                    results.Add(Path.GetRelativePath(workspace, file.FullName).Replace('\\', '/'));
                    if (results.Count >= MaxFiles)
                    {
                        break;
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (depth >= MaxDepth)
            {
                continue;
            }

            try
            {
                foreach (var child in directory.EnumerateDirectories())
                {
                    if (ComposerSuggestionRules.SkippedDirectories.Contains(child.Name)
                        || child.Attributes.HasFlag(FileAttributes.Hidden))
                    {
                        continue;
                    }

                    pending.Enqueue((child, depth + 1));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Unreadable subtree: keep the rest of the walk.
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        var snapshot = (IReadOnlyList<string>)results;
        lock (CacheLock)
        {
            Cache[workspace] = snapshot;
        }

        return snapshot;
    }

    /// <summary>Drops the cache for a workspace (new files appeared).</summary>
    public static void Invalidate(string workspace)
    {
        lock (CacheLock)
        {
            Cache.Remove(workspace);
        }
    }

    /// <summary>Path matches: prefix hits first, then contains; bounded.</summary>
    public static IReadOnlyList<string> MatchPaths(
        IReadOnlyList<string> files,
        string? query,
        int limit)
    {
        if (limit <= 0 || files.Count == 0)
        {
            return Array.Empty<string>();
        }

        var needle = query?.Trim() ?? string.Empty;
        if (needle.Length == 0)
        {
            return Array.Empty<string>();
        }

        return files
            .Select(path => (Path: path, Index: path.LastIndexOf('/') + 1))
            .Where(candidate => candidate.Path.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Path[candidate.Index..]
                .StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            .ThenBy(candidate => candidate.Path.Length)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(candidate => candidate.Path)
            .ToList();
    }

    /// <summary>
    /// Content matches grouped by file. Skips binary and oversized files and
    /// stops once <paramref name="maxGroups"/> files or
    /// <paramref name="maxLinesPerGroup"/> lines per file are reached.
    /// </summary>
    public static IReadOnlyList<(string RelativePath, IReadOnlyList<PaletteLineMatch> Lines)> SearchContents(
        string workspace,
        IReadOnlyList<string> files,
        string? query,
        int maxGroups,
        int maxLinesPerGroup)
    {
        var needle = query?.Trim() ?? string.Empty;
        if (needle.Length < 2 || maxGroups <= 0 || maxLinesPerGroup <= 0)
        {
            return Array.Empty<(string, IReadOnlyList<PaletteLineMatch>)>();
        }

        var results = new List<(string, IReadOnlyList<PaletteLineMatch>)>();
        foreach (var relative in files)
        {
            if (results.Count >= maxGroups)
            {
                break;
            }

            var fullPath = Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar));
            var matches = MatchFile(fullPath, needle, maxLinesPerGroup);
            if (matches.Count > 0)
            {
                results.Add((relative, matches));
            }
        }

        return results;
    }

    private static List<PaletteLineMatch> MatchFile(string path, string needle, int limit)
    {
        var matches = new List<PaletteLineMatch>();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
            {
                return matches;
            }

            using var reader = new StreamReader(path);
            var lineNumber = 0;
            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                if (line.Contains('\0'))
                {
                    return new List<PaletteLineMatch>();
                }

                if (line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    var trimmed = line.Trim();
                    matches.Add(new PaletteLineMatch(
                        lineNumber,
                        trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…"));
                    if (matches.Count >= limit)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Unreadable file: no matches.
        }

        return matches;
    }
}

using System.Text;

namespace Kl.Gui.Services;

public enum ImportKind
{
    Instructions,
    Skill
}

/// <summary>One safe item found in another AI app's config directory.</summary>
public sealed class ImportCandidate
{
    public ImportKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string DestinationPath { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SizeLabel { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
}

/// <summary>
/// Discovers only explicit, non-secret files in Codex/Claude homes:
/// instructions and user skills. Auth files, sessions, history and caches
/// are never opened.
/// </summary>
public sealed class ImportDiscoveryService
{
    private const long MaxInstructionsBytes = 256 * 1024;
    private const long MaxSkillBytes = 2 * 1024 * 1024;

    public ImportDiscoveryService(string? codexHome = null, string? claudeHome = null, string? klHome = null)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        CodexHome = codexHome
            ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(profile, ".codex");
        ClaudeHome = claudeHome ?? Path.Combine(profile, ".claude");
        KlHome = klHome ?? KlDataPaths.Default.HomeDirectory;
    }

    public string CodexHome { get; }
    public string ClaudeHome { get; }
    public string KlHome { get; }

    public IReadOnlyList<ImportCandidate> Discover()
    {
        var candidates = new List<ImportCandidate>();
        AddInstructions(candidates, Path.Combine(CodexHome, "AGENTS.md"), "Codex");
        AddInstructions(candidates, Path.Combine(ClaudeHome, "CLAUDE.md"), "Claude");
        AddSkills(candidates, CodexHome);
        return candidates;
    }

    public string Import(ImportCandidate candidate) => candidate.Kind switch
    {
        ImportKind.Instructions => ImportInstructions(candidate),
        ImportKind.Skill => ImportSkill(candidate),
        _ => "未知的导入类型。"
    };

    private void AddInstructions(List<ImportCandidate> candidates, string path, string source)
    {
        if (!TryGetRegularFileLength(path, MaxInstructionsBytes, out var length))
        {
            return;
        }

        candidates.Add(new ImportCandidate
        {
            Kind = ImportKind.Instructions,
            Name = Path.GetFileName(path),
            SourcePath = path,
            DestinationPath = Path.Combine(KlHome, "AGENTS.md"),
            Description = $"来自 {source} 的说明文件",
            SizeLabel = FormatSize(length),
            SizeBytes = length
        });
    }

    private void AddSkills(List<ImportCandidate> candidates, string codexHome)
    {
        var skillsRoot = Path.Combine(codexHome, "skills");
        if (!Directory.Exists(skillsRoot))
        {
            return;
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(skillsRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.') || !TrySkillSize(directory, out var size))
            {
                continue;
            }

            var manifest = Path.Combine(directory, "SKILL.md");
            if (!TryGetRegularFileLength(manifest, MaxInstructionsBytes, out _))
            {
                continue;
            }

            candidates.Add(new ImportCandidate
            {
                Kind = ImportKind.Skill,
                Name = name,
                SourcePath = directory,
                DestinationPath = Path.Combine(KlHome, "skills", name),
                Description = "Codex 用户技能",
                SizeLabel = FormatSize(size),
                SizeBytes = size
            });
        }
    }

    private string ImportInstructions(ImportCandidate candidate)
    {
        try
        {
            var content = File.ReadAllText(candidate.SourcePath).Trim();
            if (content.Length == 0)
            {
                return "说明文件为空，已跳过。";
            }

            Directory.CreateDirectory(KlHome);
            var target = candidate.DestinationPath;
            var markerPrefix = $"<!-- imported from {candidate.Name} ";
            var marker = $"{markerPrefix}{DateTimeOffset.Now:yyyy-MM-dd} -->";
            var existing = File.Exists(target) ? File.ReadAllText(target) : string.Empty;
            if (existing.Contains(markerPrefix, StringComparison.Ordinal))
            {
                return "已导入过，跳过。";
            }

            if (existing.Length > 0)
            {
                var backup = $"{target}.before-import-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
                File.Copy(target, backup, overwrite: false);
            }

            var merged = existing.Length == 0
                ? $"{marker}\n{content}\n"
                : $"{existing.TrimEnd()}\n\n{marker}\n{content}\n";
            File.WriteAllText(target, merged, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return "已合并到 AGENTS.md。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "导入失败：无法写入说明文件。";
        }
    }

    private string ImportSkill(ImportCandidate candidate)
    {
        var name = Path.GetFileName(candidate.SourcePath);
        if (!IsSafeName(name))
        {
            return "技能名称不合法，已跳过。";
        }

        var destination = Path.Combine(KlHome, "skills", name);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            return "目标技能已存在，跳过。";
        }

        var temporary = destination + ".import-tmp";
        try
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }

            var copied = CopySkillDirectory(candidate.SourcePath, temporary, 0);
            if (copied > MaxSkillBytes)
            {
                Directory.Delete(temporary, recursive: true);
                return "技能超过 2 MB，已跳过。";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(temporary, destination);
            return "已导入技能。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            return "导入失败：无法复制技能目录。";
        }
    }

    private static long CopySkillDirectory(string source, string destination, long total)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || IsSecretFile(file))
            {
                continue;
            }

            var length = new FileInfo(file).Length;
            total += length;
            if (total > MaxSkillBytes)
            {
                return total;
            }
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            total = CopySkillDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), total);
            if (total > MaxSkillBytes)
            {
                return total;
            }
        }

        return total;
    }

    private static bool TrySkillSize(string directory, out long total)
    {
        total = 0;
        try
        {
            return AddSkillSize(directory, ref total);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool AddSkillSize(string directory, ref long total)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            if (IsSecretFile(file))
            {
                continue;
            }

            total += new FileInfo(file).Length;
            if (total > MaxSkillBytes)
            {
                return false;
            }
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            if (!AddSkillSize(child, ref total))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetRegularFileLength(string path, long maxBytes, out long length)
    {
        length = 0;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            length = new FileInfo(path).Length;
            return length <= maxBytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSafeName(string name) =>
        name.Length > 0
        && name != "."
        && name != ".."
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsSecretFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("auth.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".token", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B"
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best effort; a failed cleanup does not hide the original error.
        }
    }
}

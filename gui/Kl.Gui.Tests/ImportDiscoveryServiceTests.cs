using Kl.Gui.Services;

namespace Kl.Gui.Tests;

public sealed class ImportDiscoveryServiceTests
{
    [Fact]
    public void DiscoverFindsInstructionsAndSkillsButNeverAuthFiles()
    {
        using var temp = new TempDirectory();
        var codex = Path.Combine(temp.Path, ".codex");
        var claude = Path.Combine(temp.Path, ".claude");
        var kl = Path.Combine(temp.Path, ".kl");
        Directory.CreateDirectory(Path.Combine(codex, "skills", "demo"));
        Directory.CreateDirectory(Path.Combine(codex, "skills", ".system", "builtin"));
        Directory.CreateDirectory(claude);
        File.WriteAllText(Path.Combine(codex, "AGENTS.md"), "codex instructions");
        File.WriteAllText(Path.Combine(codex, "auth.json"), """{"token":"must-not-be-read"}""");
        File.WriteAllText(Path.Combine(codex, "skills", "demo", "SKILL.md"), "# demo");
        File.WriteAllText(Path.Combine(codex, "skills", ".system", "builtin", "SKILL.md"), "# builtin");
        File.WriteAllText(Path.Combine(claude, "CLAUDE.md"), "claude instructions");

        var service = new ImportDiscoveryService(codex, claude, kl);
        var candidates = service.Discover();

        Assert.Equal(3, candidates.Count);
        Assert.Contains(candidates, item => item.Kind == ImportKind.Instructions && item.Name == "AGENTS.md");
        Assert.Contains(candidates, item => item.Kind == ImportKind.Instructions && item.Name == "CLAUDE.md");
        Assert.Contains(candidates, item => item.Kind == ImportKind.Skill && item.Name == "demo");
        Assert.DoesNotContain(candidates, item => item.Name.Contains("auth", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImportInstructionsMergesWithMarkerAndBacksUpExistingFile()
    {
        using var temp = new TempDirectory();
        var codex = Path.Combine(temp.Path, ".codex");
        var kl = Path.Combine(temp.Path, ".kl");
        Directory.CreateDirectory(codex);
        Directory.CreateDirectory(kl);
        File.WriteAllText(Path.Combine(codex, "AGENTS.md"), "source text");
        File.WriteAllText(Path.Combine(kl, "AGENTS.md"), "existing text");

        var service = new ImportDiscoveryService(codex, Path.Combine(temp.Path, ".claude"), kl);
        var candidate = service.Discover().Single(item => item.Kind == ImportKind.Instructions);

        Assert.StartsWith("已", service.Import(candidate).Message);
        var merged = File.ReadAllText(Path.Combine(kl, "AGENTS.md"));
        Assert.Contains("existing text", merged);
        Assert.Contains("source text", merged);
        Assert.Single(Directory.GetFiles(kl, "AGENTS.md.before-import-*"));

        Assert.Contains("已导入过", service.Import(candidate).Message);
        Assert.Single(
            File.ReadAllLines(Path.Combine(kl, "AGENTS.md")),
            line => line.Contains("imported from AGENTS.md", StringComparison.Ordinal));
    }

    [Fact]
    public void ImportSkillCopiesSkillAndSkipsExistingDestination()
    {
        using var temp = new TempDirectory();
        var codex = Path.Combine(temp.Path, ".codex");
        var kl = Path.Combine(temp.Path, ".kl");
        Directory.CreateDirectory(Path.Combine(codex, "skills", "demo"));
        File.WriteAllText(Path.Combine(codex, "skills", "demo", "SKILL.md"), "# demo");
        File.WriteAllText(Path.Combine(codex, "skills", "demo", "helper.txt"), "helper");
        File.WriteAllText(Path.Combine(codex, "skills", "demo", "auth.json"), "{}");

        var service = new ImportDiscoveryService(codex, Path.Combine(temp.Path, ".claude"), kl);
        var candidate = service.Discover().Single(item => item.Kind == ImportKind.Skill);

        Assert.StartsWith("已", service.Import(candidate).Message);
        Assert.Equal("# demo", File.ReadAllText(Path.Combine(kl, "skills", "demo", "SKILL.md")));
        Assert.Equal("helper", File.ReadAllText(Path.Combine(kl, "skills", "demo", "helper.txt")));
        Assert.False(File.Exists(Path.Combine(kl, "skills", "demo", "auth.json")));

        Assert.Contains("已存在", service.Import(candidate).Message);
    }
}

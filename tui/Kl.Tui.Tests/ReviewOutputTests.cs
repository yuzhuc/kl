using System.Text.Json;
using Xunit;

namespace Kl.Tui.Tests;

public sealed class ReviewOutputTests
{
    private static JsonElement Result(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void RendersRequiredFindingFieldsAndLowConfidenceMarker()
    {
        using var document = JsonDocument.Parse("""
            {
              "mode": "strict",
              "summary": "1 changed files; 1 high, 1 low",
              "changed_files": 1,
              "findings": [
                {
                  "rule": "secret-openai",
                  "source": "secret-scan",
                  "file": "src/a.rs",
                  "line": 3,
                  "severity": "high",
                  "confidence": 0.99,
                  "evidence": "sk-abc<redacted>",
                  "recommendation": "rotate the key",
                  "introduced": true,
                  "low_confidence": false
                },
                {
                  "rule": "llm-review-unavailable",
                  "source": "llm",
                  "file": "",
                  "line": 0,
                  "severity": "low",
                  "confidence": 0.4,
                  "evidence": "no provider",
                  "recommendation": "configure a provider",
                  "introduced": true,
                  "low_confidence": true
                }
              ]
            }
            """);
        var text = ReviewOutput.Render(document.RootElement);
        Assert.Contains("[high] src/a.rs:3", text);
        Assert.Contains("confidence=0.99", text);
        Assert.Contains("introduced", text);
        Assert.Contains("evidence: sk-abc<redacted>", text);
        Assert.Contains("fix: rotate the key", text);
        Assert.Contains("[low] (workspace)", text);
        Assert.Contains("LOW-CONFIDENCE", text);
        Assert.Equal(1, ReviewOutput.ExitCode(document.RootElement));
    }

    [Fact]
    public void NoFindingsReturnsSuccess()
    {
        var result = Result("""
            {
              "mode": "quick",
              "summary": "0 changed files; 0 critical, 0 high, 0 medium, 0 low, 0 info",
              "changed_files": 0,
              "findings": []
            }
            """);
        Assert.Contains("no findings", ReviewOutput.Render(result));
        Assert.Equal(0, ReviewOutput.ExitCode(result));
    }
}

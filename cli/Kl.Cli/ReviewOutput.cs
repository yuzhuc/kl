using System.Text;
using System.Text.Json;

namespace Kl.Cli;

public static class ReviewOutput
{
    public static string Render(JsonElement result)
    {
        var builder = new StringBuilder();
        var mode = result.GetProperty("mode").GetString() ?? "standard";
        var summary = result.GetProperty("summary").GetString() ?? string.Empty;
        builder.AppendLine($"Review ({mode}): {summary}");
        var findings = result.GetProperty("findings");
        if (findings.GetArrayLength() == 0)
        {
            builder.AppendLine("no findings");
            return builder.ToString().TrimEnd();
        }

        foreach (var finding in findings.EnumerateArray())
        {
            var severity = finding.GetProperty("severity").GetString() ?? "info";
            var file = finding.GetProperty("file").GetString() ?? string.Empty;
            var line = finding.GetProperty("line").GetInt32();
            var confidence = finding.GetProperty("confidence").GetSingle();
            var rule = finding.GetProperty("rule").GetString() ?? "unknown";
            var source = finding.GetProperty("source").GetString() ?? "unknown";
            var introduced = finding.GetProperty("introduced").GetBoolean();
            var low = finding.GetProperty("low_confidence").GetBoolean();
            var location = string.IsNullOrWhiteSpace(file)
                ? "(workspace)"
                : (line > 0 ? $"{file}:{line}" : file);
            var marker = low ? " LOW-CONFIDENCE" : string.Empty;
            builder.AppendLine(
                $"[{severity}] {location} confidence={confidence:0.00} " +
                $"{rule} ({source}){(introduced ? " introduced" : " existing")}{marker}");
            var evidence = finding.GetProperty("evidence").GetString();
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                builder.AppendLine($"  evidence: {evidence}");
            }
            var recommendation = finding.GetProperty("recommendation").GetString();
            if (!string.IsNullOrWhiteSpace(recommendation))
            {
                builder.AppendLine($"  fix: {recommendation}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    public static int ExitCode(JsonElement result)
    {
        foreach (var finding in result.GetProperty("findings").EnumerateArray())
        {
            var severity = finding.GetProperty("severity").GetString();
            if (severity is "critical" or "high")
            {
                return 1;
            }
        }
        return 0;
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Kl.Client;
using Kl.Gui.Models;
using Kl.Gui.Services;

namespace Kl.Gui.ViewModels;

/// <summary>One model row on a provider's detail page.</summary>
public sealed class ProviderModelRow : ObservableObject
{
    private bool _enabled;

    public ProviderModelRow(
        string id,
        string protocol,
        string url,
        int contextWindow,
        double compactThreshold,
        int maxOutputTokens,
        bool enabled = true,
        string reasoningEffort = "default",
        IReadOnlyList<string>? reasoningEfforts = null,
        string name = "")
    {
        Id = id;
        Name = name;
        Protocol = protocol;
        Url = url;
        ContextWindow = contextWindow;
        CompactThreshold = compactThreshold;
        MaxOutputTokens = maxOutputTokens;
        ReasoningEffort = reasoningEffort;
        ReasoningEfforts = reasoningEfforts ?? Array.Empty<string>();
        _enabled = enabled;
    }

    public string Id { get; }
    public string Name { get; }
    public string Protocol { get; }
    public string Url { get; }
    public int ContextWindow { get; }
    public double CompactThreshold { get; }
    public int MaxOutputTokens { get; }
    public string ReasoningEffort { get; }
    public IReadOnlyList<string> ReasoningEfforts { get; }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    public string ProtocolLabel => Protocol switch
    {
        "anthropic" => "Anthropic",
        "openai-responses" => "OpenAI Responses",
        _ => "OpenAI Chat"
    };

    public string ReasoningLabel => ReasoningEffort switch
    {
        "none" => "思考 none",
        "minimal" => "思考 minimal",
        "low" => "思考 low",
        "medium" => "思考 medium",
        "high" => "思考 high",
        "xhigh" => "思考 xhigh",
        "max" => "思考 max",
        "ultra" => "思考 ultra",
        _ => "思考 默认"
    };

    public string Summary =>
        $"{ProtocolLabel} · {ReasoningLabel} · {FormatTokens(ContextWindow)} 上下文 · {FormatTokens(MaxOutputTokens)} 输出";

    private static string FormatTokens(int value) =>
        value >= 1000
            ? $"{value / 1000.0:0.#}k"
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

using System.Globalization;
using System.Text.Json;

namespace Kl.Cli;

public enum UiLanguage
{
    English,
    ChineseSimplified
}

public enum SettingsGroup
{
    Model,
    Agent,
    Plugins,
    Skills,
    Config
}

public enum SettingsPane
{
    Groups,
    Content
}

public enum SettingsView
{
    Providers,
    ProviderEditor,
    ModelEditor
}

public enum SettingsAddressMode
{
    Base,
    Full
}

public enum SettingsCommandKind
{
    None,
    Close,
    SaveProvider,
    SaveProviderAndClose,
    SetDefaultModel,
    SetLanguage,
    ToggleProvider,
    SetPluginEnabled,
    ReloadPlugin,
    Reload
}

public sealed record SettingsCommand(
    SettingsCommandKind Kind,
    string? Provider = null,
    string? Model = null,
    bool? Enabled = null,
    string? Plugin = null,
    UiLanguage? Language = null)
{
    public static readonly SettingsCommand None = new(SettingsCommandKind.None);
}

public enum PluginAction
{
    Enable,
    Disable,
    Reload
}

public static class SettingsUrls
{
    public const string DefaultBase = "https://opencode.ai/zen/go/v1";

    public static (string Chat, string Anthropic, string Responses) ExpandBase(string baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        return (
            trimmed + "/chat/completions",
            trimmed + "/messages",
            trimmed + "/responses");
    }

    public static Dictionary<string, string> Build(
        SettingsAddressMode mode,
        string baseUrl,
        string chat,
        string anthropic,
        string responses)
    {
        if (mode == SettingsAddressMode.Base)
        {
            var (expandedChat, expandedAnthropic, expandedResponses) =
                ExpandBase(baseUrl);
            return new Dictionary<string, string>
            {
                ["openai-chat"] = expandedChat,
                ["anthropic"] = expandedAnthropic,
                ["openai-responses"] = expandedResponses
            };
        }

        return new Dictionary<string, string>
        {
            ["openai-chat"] = chat,
            ["anthropic"] = anthropic,
            ["openai-responses"] = responses
        };
    }
}

public sealed class SettingsText
{
    public SettingsText(string value = "")
    {
        Value = value;
        Cursor = value.Length;
    }

    public string Value { get; private set; }
    public int Cursor { get; private set; }

    public void Set(string value)
    {
        Value = value ?? string.Empty;
        Cursor = Value.Length;
    }

    public void Insert(char character)
    {
        if (char.IsControl(character))
        {
            return;
        }
        Value = Value.Insert(Cursor, character.ToString());
        Cursor++;
    }

    public void Backspace()
    {
        if (Cursor == 0)
        {
            return;
        }
        Value = Value.Remove(Cursor - 1, 1);
        Cursor--;
    }

    public void Delete()
    {
        if (Cursor >= Value.Length)
        {
            return;
        }
        Value = Value.Remove(Cursor, 1);
    }

    public void Left() => Cursor = Math.Max(0, Cursor - 1);
    public void Right() => Cursor = Math.Min(Value.Length, Cursor + 1);
    public void Home() => Cursor = 0;
    public void End() => Cursor = Value.Length;

    public override string ToString() => Value;
}

public sealed record SettingsModel(
    string Provider,
    string Id,
    string Protocol,
    int ContextWindow,
    double CompactThreshold,
    int MaxOutputTokens,
    string Url,
    bool KeyConfigured);

public sealed record SettingsProvider(
    string Id,
    bool Enabled,
    bool? KeyConfigured,
    int ModelCount,
    string ProtocolSummary,
    SettingsAddressMode AddressMode,
    string BaseUrl,
    string ChatUrl,
    string AnthropicUrl,
    string ResponsesUrl,
    IReadOnlyList<SettingsModel> Models);

public sealed record SettingsPlugin(
    string Id,
    string Version,
    string Kind,
    bool Enabled,
    bool Healthy,
    string Detail,
    string? ToolName,
    IReadOnlyList<string> Models);

public sealed record SettingsSnapshot(
    string ActiveProvider,
    string ActiveModel,
    IReadOnlyList<SettingsProvider> Providers,
    IReadOnlyList<SettingsModel> Models,
    IReadOnlyList<SettingsPlugin> Plugins)
{
    public static SettingsSnapshot Empty { get; } =
        new(string.Empty, string.Empty, [], [], []);

    public static SettingsSnapshot FromJson(
        JsonElement providerList,
        JsonElement config,
        JsonElement pluginList = default)
    {
        var configuredProviders = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty("config", out var configBody)
            && configBody.ValueKind == JsonValueKind.Object
            && configBody.TryGetProperty("providers", out var providerMap)
            && providerMap.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in providerMap.EnumerateObject())
            {
                var enabled = !entry.Value.TryGetProperty("enabled", out var enabledValue)
                    || enabledValue.ValueKind != JsonValueKind.False;
                configuredProviders[entry.Name] = enabled;
            }
        }

        var models = new List<SettingsModel>();
        if (providerList.ValueKind == JsonValueKind.Object
            && providerList.TryGetProperty("models", out var modelArray)
            && modelArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in modelArray.EnumerateArray())
            {
                var provider = GetString(value, "provider");
                if (string.IsNullOrWhiteSpace(provider))
                {
                    continue;
                }
                if (configuredProviders.TryGetValue(provider, out var providerEnabled)
                    && !providerEnabled)
                {
                    continue;
                }
                if (value.TryGetProperty("enabled", out var modelEnabled)
                    && modelEnabled.ValueKind == JsonValueKind.False)
                {
                    continue;
                }
                var protocol = GetString(value, "protocol");
                if (protocol is not ("openai-chat" or "anthropic" or "openai-responses"))
                {
                    continue;
                }

                models.Add(new SettingsModel(
                    provider,
                    GetString(value, "model"),
                    protocol,
                    GetInt(value, "context_window"),
                    GetDouble(value, "compact_threshold"),
                    GetInt(value, "max_output_tokens"),
                    GetString(value, "url"),
                    GetBool(value, "key_configured")));
            }
        }

        var grouped = models
            .GroupBy(model => model.Provider)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(model => model.Id, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
        var providers = new List<SettingsProvider>();
        foreach (var (id, providerModels) in grouped)
        {
            var protocols = providerModels
                .Select(model => model.Protocol)
                .Where(protocol => !string.IsNullOrWhiteSpace(protocol))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(protocol => protocol, StringComparer.Ordinal)
                .ToArray();
            var address = InferAddress(providerModels);
            providers.Add(new SettingsProvider(
                id,
                true,
                providerModels[0].KeyConfigured,
                providerModels.Count,
                string.Join(", ", protocols),
                address.Mode,
                address.BaseUrl,
                address.ChatUrl,
                address.AnthropicUrl,
                address.ResponsesUrl,
                providerModels));
        }

        foreach (var (id, enabled) in configuredProviders)
        {
            if (grouped.ContainsKey(id))
            {
                continue;
            }
            providers.Add(new SettingsProvider(
                id,
                enabled,
                null,
                0,
                string.Empty,
                SettingsAddressMode.Base,
                SettingsUrls.DefaultBase,
                string.Empty,
                string.Empty,
                string.Empty,
                []));
        }

        return new SettingsSnapshot(
            GetString(providerList, "active_provider"),
            GetString(providerList, "active_model"),
            providers.OrderBy(provider => provider.Id, StringComparer.Ordinal).ToList(),
            models.OrderBy(model => model.Provider, StringComparer.Ordinal)
                .ThenBy(model => model.Id, StringComparer.Ordinal)
                .ToList(),
            ParsePlugins(pluginList));
    }

    private static IReadOnlyList<SettingsPlugin> ParsePlugins(JsonElement pluginList)
    {
        if (pluginList.ValueKind != JsonValueKind.Object
            || !pluginList.TryGetProperty("plugins", out var plugins)
            || plugins.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<SettingsPlugin>();
        foreach (var value in plugins.EnumerateArray())
        {
            var id = GetString(value, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            result.Add(new SettingsPlugin(
                id,
                GetString(value, "version"),
                GetString(value, "kind"),
                GetBool(value, "enabled"),
                GetBool(value, "healthy"),
                GetString(value, "detail"),
                GetOptionalString(value, "tool_name"),
                GetStringArray(value, "models")));
        }
        return result.OrderBy(plugin => plugin.Id, StringComparer.Ordinal).ToList();
    }

    private static (
        SettingsAddressMode Mode,
        string BaseUrl,
        string ChatUrl,
        string AnthropicUrl,
        string ResponsesUrl) InferAddress(IReadOnlyList<SettingsModel> models)
    {
        var chat = models.FirstOrDefault(model =>
            model.Protocol == "openai-chat" && !string.IsNullOrWhiteSpace(model.Url))?.Url
            ?? string.Empty;
        var anthropic = models.FirstOrDefault(model =>
            model.Protocol == "anthropic" && !string.IsNullOrWhiteSpace(model.Url))?.Url
            ?? string.Empty;
        var responses = models.FirstOrDefault(model =>
            model.Protocol == "openai-responses" && !string.IsNullOrWhiteSpace(model.Url))?.Url
            ?? string.Empty;
        var urls = new (string Protocol, string Url)[]
        {
            ("openai-chat", chat),
            ("anthropic", anthropic),
            ("openai-responses", responses)
        }.Where(item => !string.IsNullOrWhiteSpace(item.Url)).ToArray();
        if (urls.Length == 0)
        {
            return (
                SettingsAddressMode.Base,
                SettingsUrls.DefaultBase,
                chat,
                anthropic,
                responses);
        }

        string? inferredBase = null;
        foreach (var (protocol, url) in urls)
        {
            var suffix = protocol switch
            {
                "openai-chat" => "/chat/completions",
                "anthropic" => "/messages",
                "openai-responses" => "/responses",
                _ => null
            };
            if (suffix is null
                || !url.EndsWith(suffix, StringComparison.Ordinal)
                || url.Length <= suffix.Length)
            {
                return (
                    SettingsAddressMode.Full,
                    SettingsUrls.DefaultBase,
                    chat,
                    anthropic,
                    responses);
            }

            var candidate = url[..^suffix.Length].TrimEnd('/');
            if (candidate.Length == 0)
            {
                return (
                    SettingsAddressMode.Full,
                    SettingsUrls.DefaultBase,
                    chat,
                    anthropic,
                    responses);
            }
            if (inferredBase is null)
            {
                inferredBase = candidate;
            }
            else if (!string.Equals(inferredBase, candidate, StringComparison.Ordinal))
            {
                return (
                    SettingsAddressMode.Full,
                    SettingsUrls.DefaultBase,
                    chat,
                    anthropic,
                    responses);
            }
        }

        return (
            SettingsAddressMode.Base,
            inferredBase ?? SettingsUrls.DefaultBase,
            chat,
            anthropic,
            responses);
    }

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? GetOptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private static int GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static double GetDouble(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.TryGetDouble(out var result)
            ? result
            : 0;

    private static bool GetBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;
}

public sealed class SettingsModelDraft
{
    private static readonly string[] SupportedProtocols =
    [
        "openai-chat",
        "anthropic",
        "openai-responses"
    ];

    public SettingsText Id { get; } = new();
    public string Protocol { get; set; } = "openai-chat";
    public SettingsText ContextWindow { get; } = new("128000");
    public SettingsText CompactThreshold { get; } = new("0.8");
    public SettingsText MaxOutputTokens { get; } = new("4096");
    public string Url { get; set; } = string.Empty;

    public static IReadOnlyList<string> Protocols => SupportedProtocols;

    public static SettingsModelDraft From(SettingsModel model)
    {
        var draft = new SettingsModelDraft
        {
            Protocol = SupportedProtocols.Contains(model.Protocol, StringComparer.Ordinal)
                ? model.Protocol
                : SupportedProtocols[0],
            Url = model.Url
        };
        draft.Id.Set(model.Id);
        draft.ContextWindow.Set(model.ContextWindow.ToString(CultureInfo.InvariantCulture));
        draft.CompactThreshold.Set(model.CompactThreshold.ToString(CultureInfo.InvariantCulture));
        draft.MaxOutputTokens.Set(model.MaxOutputTokens.ToString(CultureInfo.InvariantCulture));
        return draft;
    }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Id.Value))
        {
            return "model id is required";
        }
        if (!SupportedProtocols.Contains(Protocol, StringComparer.Ordinal))
        {
            return "protocol must be openai-chat, anthropic, or openai-responses";
        }
        if (!int.TryParse(
                ContextWindow.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var contextWindow)
            || contextWindow <= 0)
        {
            return "context_window must be a positive integer";
        }
        if (!double.TryParse(
                CompactThreshold.Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var threshold)
            || threshold < 0
            || threshold > 1)
        {
            return "compact_threshold must be between 0 and 1";
        }
        if (!int.TryParse(
                MaxOutputTokens.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var maxOutput)
            || maxOutput <= 0)
        {
            return "max_output_tokens must be a positive integer";
        }
        return null;
    }

    public Dictionary<string, object?> ToParameter()
    {
        var model = new Dictionary<string, object?>
        {
            ["id"] = Id.Value.Trim(),
            ["protocol"] = Protocol,
            ["context_window"] = int.Parse(
                ContextWindow.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture),
            ["compact_threshold"] = double.Parse(
                CompactThreshold.Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture),
            ["max_output_tokens"] = int.Parse(
                MaxOutputTokens.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(Url))
        {
            model["url"] = Url;
        }
        return model;
    }
}

public sealed class ProviderDraft
{
    public string OriginalId { get; private set; } = string.Empty;
    public SettingsText Id { get; } = new();
    public bool Enabled { get; set; } = true;
    public bool KeyConfigured { get; set; }
    public SettingsAddressMode AddressMode { get; set; } = SettingsAddressMode.Base;
    public SettingsText BaseUrl { get; } = new(SettingsUrls.DefaultBase);
    public SettingsText ChatUrl { get; } = new();
    public SettingsText AnthropicUrl { get; } = new();
    public SettingsText ResponsesUrl { get; } = new();
    public SettingsText ApiKey { get; } = new();
    public List<SettingsModelDraft> Models { get; } = [];

    public bool IsNew => OriginalId.Length == 0;
    public string EffectiveId => Id.Value.Trim();

    public static ProviderDraft New() => new();

    public static ProviderDraft From(SettingsProvider provider)
    {
        var draft = new ProviderDraft
        {
            OriginalId = provider.Id,
            Enabled = provider.Enabled,
            KeyConfigured = provider.KeyConfigured == true,
            AddressMode = provider.AddressMode
        };
        draft.Id.Set(provider.Id);
        draft.BaseUrl.Set(provider.BaseUrl);
        draft.ChatUrl.Set(provider.ChatUrl);
        draft.AnthropicUrl.Set(provider.AnthropicUrl);
        draft.ResponsesUrl.Set(provider.ResponsesUrl);
        foreach (var model in provider.Models)
        {
            draft.Models.Add(SettingsModelDraft.From(model));
        }
        return draft;
    }

    public string? Validate()
    {
        if (EffectiveId.Length == 0)
        {
            return "provider id is required";
        }
        if (AddressMode == SettingsAddressMode.Base)
        {
            if (string.IsNullOrWhiteSpace(BaseUrl.Value))
            {
                return "base URL is required";
            }
        }
        else if (string.IsNullOrWhiteSpace(ChatUrl.Value)
            || string.IsNullOrWhiteSpace(AnthropicUrl.Value)
            || string.IsNullOrWhiteSpace(ResponsesUrl.Value))
        {
            return "full mode requires all three complete URLs";
        }
        if (Models.Count == 0)
        {
            return "at least one model is required";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in Models)
        {
            var error = model.Validate();
            if (error is not null)
            {
                return error;
            }
            if (!seen.Add(model.Id.Value.Trim()))
            {
                return $"duplicate model id: {model.Id.Value.Trim()}";
            }
        }
        return null;
    }

    public Dictionary<string, object?> ToUpsert()
    {
        var protocols = SettingsUrls.Build(
            AddressMode,
            BaseUrl.Value,
            ChatUrl.Value,
            AnthropicUrl.Value,
            ResponsesUrl.Value);
        return new Dictionary<string, object?>
        {
            ["id"] = EffectiveId,
            ["protocols"] = protocols,
            ["models"] = Models.Select(model => model.ToParameter()).ToList(),
            ["enabled"] = Enabled
        };
    }

    public string MaskedKey(bool focused)
    {
        var value = ApiKey.Value.Length > 0
            ? new string('*', ApiKey.Value.Length)
            : KeyConfigured
                ? "********"
                : string.Empty;
        if (!focused)
        {
            return value.Length > 0 ? value : "(not set)";
        }

        var cursor = Math.Clamp(ApiKey.Cursor, 0, value.Length);
        var visible = value.Insert(cursor, "|");
        return visible.Length > 0 ? visible : "|";
    }

    public static string Redact(string message, string secret) =>
        string.IsNullOrEmpty(secret)
            ? message
            : message.Replace(secret, "***", StringComparison.Ordinal);
}

public sealed record ProviderEditorRow(
    ProviderRowKind Kind,
    int ModelIndex = -1);

public enum ProviderRowKind
{
    Id,
    Enabled,
    Mode,
    BaseUrl,
    ChatUrl,
    AnthropicUrl,
    ResponsesUrl,
    ApiKey,
    ModelsHeader,
    Model,
    AddModel
}

public enum ModelRowKind
{
    Id,
    Protocol,
    ContextWindow,
    CompactThreshold,
    MaxOutputTokens
}

public sealed class SettingsTuiState
{
    private int _modelEditIndex = -1;

    public IReadOnlyList<SettingsGroup> Groups { get; } =
    [
        SettingsGroup.Model,
        SettingsGroup.Agent,
        SettingsGroup.Plugins,
        SettingsGroup.Skills,
        SettingsGroup.Config
    ];

    public SettingsSnapshot Snapshot { get; private set; } = SettingsSnapshot.Empty;
    public int GroupIndex { get; private set; }
    public SettingsGroup Group => Groups[Math.Clamp(GroupIndex, 0, Groups.Count - 1)];
    public SettingsPane Pane { get; private set; } = SettingsPane.Groups;
    public SettingsView View { get; private set; } = SettingsView.Providers;
    public int ListRow { get; private set; }
    public int ProviderEditorRowIndex { get; private set; }
    public int ModelEditorRowIndex { get; private set; }
    public int RightScroll { get; set; }
    public ProviderDraft? Draft { get; private set; }
    public SettingsModelDraft? ModelDraft { get; private set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public UiLanguage Language { get; private set; } = UiLanguage.English;
    public bool IsLoading { get; private set; }
    public PluginAction? PendingPluginAction { get; private set; }
    public string? PendingPluginId { get; private set; }

    public ProviderEditorRow CurrentProviderRow =>
        ProviderEditorRows.Count == 0
            ? new ProviderEditorRow(ProviderRowKind.Id)
            : ProviderEditorRows[
                Math.Clamp(ProviderEditorRowIndex, 0, ProviderEditorRows.Count - 1)];

    public IReadOnlyList<ProviderEditorRow> ProviderEditorRows
    {
        get
        {
            if (Draft is null)
            {
                return [];
            }
            var rows = new List<ProviderEditorRow>
            {
                new(ProviderRowKind.Id),
                new(ProviderRowKind.Enabled),
                new(ProviderRowKind.Mode)
            };
            if (Draft.AddressMode == SettingsAddressMode.Base)
            {
                rows.Add(new ProviderEditorRow(ProviderRowKind.BaseUrl));
            }
            else
            {
                rows.Add(new ProviderEditorRow(ProviderRowKind.ChatUrl));
                rows.Add(new ProviderEditorRow(ProviderRowKind.AnthropicUrl));
                rows.Add(new ProviderEditorRow(ProviderRowKind.ResponsesUrl));
            }
            rows.Add(new ProviderEditorRow(ProviderRowKind.ApiKey));
            rows.Add(new ProviderEditorRow(ProviderRowKind.ModelsHeader));
            for (var index = 0; index < Draft.Models.Count; index++)
            {
                rows.Add(new ProviderEditorRow(ProviderRowKind.Model, index));
            }
            rows.Add(new ProviderEditorRow(ProviderRowKind.AddModel));
            return rows;
        }
    }

    public void SetLanguage(UiLanguage language)
    {
        Language = language;
    }

    private SettingsCommand ToggleLanguage()
    {
        SetLanguage(Language == UiLanguage.English
            ? UiLanguage.ChineseSimplified
            : UiLanguage.English);
        Error = null;
        Status = null;
        return new SettingsCommand(
            SettingsCommandKind.SetLanguage,
            Language: Language);
    }
    public void BeginLoad()
    {
        IsLoading = true;
        Status = "loading settings...";
        Error = null;
        ClearPluginPending();
    }

    public void FailLoad(string message)
    {
        IsLoading = false;
        Status = null;
        Error = message;
    }

    public void Load(SettingsSnapshot snapshot, string? selectedProvider = null)
    {
        Snapshot = snapshot;
        GroupIndex = 0;
        View = SettingsView.Providers;
        Draft = null;
        ModelDraft = null;
        _modelEditIndex = -1;
        RightScroll = 0;
        IsLoading = false;
        Status = null;
        Error = null;
        ClearPluginPending();
        if (selectedProvider is null)
        {
            Pane = SettingsPane.Groups;
            ListRow = 0;
            return;
        }

        Pane = SettingsPane.Content;
        var index = snapshot.Providers
            .Select((provider, providerIndex) => (provider, providerIndex))
            .FirstOrDefault(item => item.provider.Id == selectedProvider)
            .providerIndex;
        ListRow = Math.Clamp(index, 0, Math.Max(0, snapshot.Providers.Count - 1));
    }

    public void LoadPlugins(SettingsSnapshot snapshot, string? selectedPlugin = null)
    {
        Snapshot = snapshot;
        GroupIndex = Groups.ToList().IndexOf(SettingsGroup.Plugins);
        View = SettingsView.Providers;
        Pane = SettingsPane.Content;
        Draft = null;
        ModelDraft = null;
        _modelEditIndex = -1;
        RightScroll = 0;
        IsLoading = false;
        Status = null;
        Error = null;
        ClearPluginPending();

        var index = string.IsNullOrWhiteSpace(selectedPlugin)
            ? 0
            : snapshot.Plugins
                .Select((plugin, pluginIndex) => (plugin, pluginIndex))
                .FirstOrDefault(item => item.plugin.Id == selectedPlugin)
                .pluginIndex;
        ListRow = Math.Clamp(index, 0, Math.Max(0, snapshot.Plugins.Count - 1));
    }

    public void OpenNewProvider()
    {
        Draft = ProviderDraft.New();
        View = SettingsView.ProviderEditor;
        Pane = SettingsPane.Content;
        ProviderEditorRowIndex = 0;
        Status = null;
        Error = null;
    }

    public void OpenProviderEditor(SettingsProvider provider)
    {
        if (!provider.Enabled)
        {
            Error = "enable this provider first; disabled provider models are hidden";
            Status = null;
            return;
        }
        Draft = ProviderDraft.From(provider);
        View = SettingsView.ProviderEditor;
        Pane = SettingsPane.Content;
        ProviderEditorRowIndex = 0;
        Status = null;
        Error = null;
    }

    public void OpenSelectedProvider()
    {
        if (SelectedProvider is { } provider)
        {
            OpenProviderEditor(provider);
        }
    }

    public void AddModel()
    {
        if (Draft is null)
        {
            return;
        }
        ModelDraft = new SettingsModelDraft();
        _modelEditIndex = -1;
        View = SettingsView.ModelEditor;
        ModelEditorRowIndex = 0;
        Status = null;
        Error = null;
    }

    public void EditModel(int index)
    {
        if (Draft is null || index < 0 || index >= Draft.Models.Count)
        {
            return;
        }
        var source = Draft.Models[index];
        ModelDraft = new SettingsModelDraft
        {
            Protocol = source.Protocol,
            Url = source.Url
        };
        ModelDraft.Id.Set(source.Id.Value);
        ModelDraft.ContextWindow.Set(source.ContextWindow.Value);
        ModelDraft.CompactThreshold.Set(source.CompactThreshold.Value);
        ModelDraft.MaxOutputTokens.Set(source.MaxOutputTokens.Value);
        _modelEditIndex = index;
        View = SettingsView.ModelEditor;
        ModelEditorRowIndex = 0;
        Status = null;
        Error = null;
    }

    public SettingsModel? SelectedModel
    {
        get
        {
            var modelIndex = ListRow - Snapshot.Providers.Count;
            return modelIndex >= 0 && modelIndex < Snapshot.Models.Count
                ? Snapshot.Models[modelIndex]
                : null;
        }
    }

    public SettingsPlugin? SelectedPlugin =>
        ListRow >= 0 && ListRow < Snapshot.Plugins.Count
            ? Snapshot.Plugins[ListRow]
            : null;

    public SettingsProvider? SelectedProvider
    {
        get
        {
            if (ListRow >= 0 && ListRow < Snapshot.Providers.Count)
            {
                return Snapshot.Providers[ListRow];
            }
            if (SelectedModel is { } model)
            {
                return Snapshot.Providers.FirstOrDefault(provider => provider.Id == model.Provider);
            }
            return null;
        }
    }

    public SettingsCommand HandleKey(ConsoleKeyInfo key)
    {
        var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
        if (key.Key == ConsoleKey.F2)
        {
            return SaveAndExit();
        }
        if (control && (key.Key == ConsoleKey.S || key.KeyChar == '\u0013'))
        {
            return Save();
        }
        if (control && key.Key == ConsoleKey.C)
        {
            return new SettingsCommand(SettingsCommandKind.Close);
        }

        if (Pane == SettingsPane.Content && Group == SettingsGroup.Config)
        {
            if (key.Key == ConsoleKey.Enter
                || key.KeyChar is ' ' or 'l' or 'L')
            {
                return ToggleLanguage();
            }
        }

        if (Pane == SettingsPane.Content && Group == SettingsGroup.Plugins)
        {
            if (key.KeyChar is 'e' or 'E')
            {
                return RequestPluginAction(PluginAction.Enable);
            }
            if (key.KeyChar is 'd' or 'D')
            {
                return RequestPluginAction(PluginAction.Disable);
            }
            if (key.KeyChar is 'r' or 'R')
            {
                return RequestPluginAction(PluginAction.Reload);
            }
            if (key.KeyChar == ' ' && SelectedPlugin is { } plugin)
            {
                return RequestPluginAction(
                    plugin.Enabled ? PluginAction.Disable : PluginAction.Enable);
            }
        }

        if (Pane == SettingsPane.Content
            && View == SettingsView.Providers
            && Group == SettingsGroup.Model)
        {
            if (key.KeyChar is 'n' or 'N')
            {
                OpenNewProvider();
                return SettingsCommand.None;
            }
            if (key.KeyChar is 'e' or 'E')
            {
                if (SelectedModel is { } model)
                {
                    OpenProviderEditorForModel(model);
                }
                else
                {
                    OpenSelectedProvider();
                }
                return SettingsCommand.None;
            }
            if (key.KeyChar is 'a' or 'A')
            {
                AddModelToSelectedProvider();
                return SettingsCommand.None;
            }
            if (key.KeyChar == ' ' && SelectedProvider is { } selected)
            {
                return new SettingsCommand(
                    SettingsCommandKind.ToggleProvider,
                    selected.Id,
                    Enabled: !selected.Enabled);
            }
        }
        else if (Pane == SettingsPane.Content && View == SettingsView.ProviderEditor)
        {
            var row = CurrentProviderRow;
            if (key.KeyChar is 'a' or 'A'
                && row.Kind is ProviderRowKind.ModelsHeader
                    or ProviderRowKind.Model
                    or ProviderRowKind.AddModel)
            {
                AddModel();
                return SettingsCommand.None;
            }
            if (key.KeyChar is 'e' or 'E' && row.Kind == ProviderRowKind.Model)
            {
                EditModel(row.ModelIndex);
                return SettingsCommand.None;
            }
        }

        switch (key.Key)
        {
            case ConsoleKey.Tab:
                TogglePane();
                return SettingsCommand.None;
            case ConsoleKey.UpArrow:
                Move(-1);
                return SettingsCommand.None;
            case ConsoleKey.DownArrow:
                Move(1);
                return SettingsCommand.None;
            case ConsoleKey.Escape:
                return Back();
            case ConsoleKey.Enter:
                return Confirm();
            case ConsoleKey.Backspace:
                Edit(field => field.Backspace());
                return SettingsCommand.None;
            case ConsoleKey.Delete:
                Edit(field => field.Delete());
                return SettingsCommand.None;
            case ConsoleKey.LeftArrow:
                Edit(field => field.Left());
                return SettingsCommand.None;
            case ConsoleKey.RightArrow:
                Edit(field => field.Right());
                return SettingsCommand.None;
            case ConsoleKey.Home:
                Edit(field => field.Home());
                return SettingsCommand.None;
            case ConsoleKey.End:
                Edit(field => field.End());
                return SettingsCommand.None;
        }

        if (!control
            && (key.Modifiers & ConsoleModifiers.Alt) == 0
            && key.KeyChar >= ' '
            && key.KeyChar != '\u007f')
        {
            EditInsert(key.KeyChar);
        }
        return SettingsCommand.None;
    }

    private SettingsCommand Save()
    {
        if (Group != SettingsGroup.Model)
        {
            return SettingsCommand.None;
        }
        if (View == SettingsView.ModelEditor)
        {
            return SaveModelDraft()
                ? new SettingsCommand(SettingsCommandKind.SaveProvider)
                : SettingsCommand.None;
        }
        if (View == SettingsView.ProviderEditor)
        {
            return new SettingsCommand(SettingsCommandKind.SaveProvider);
        }
        return new SettingsCommand(SettingsCommandKind.Reload);
    }

    private SettingsCommand SaveAndExit()
    {
        if (Group != SettingsGroup.Model)
        {
            return new SettingsCommand(SettingsCommandKind.Close);
        }
        if (View == SettingsView.ModelEditor)
        {
            return SaveModelDraft()
                ? new SettingsCommand(SettingsCommandKind.SaveProviderAndClose)
                : SettingsCommand.None;
        }
        if (View == SettingsView.ProviderEditor)
        {
            return new SettingsCommand(SettingsCommandKind.SaveProviderAndClose);
        }
        return new SettingsCommand(SettingsCommandKind.Close);
    }

    private void TogglePane()
    {
        Pane = Pane == SettingsPane.Groups
            ? SettingsPane.Content
            : SettingsPane.Groups;
    }

    private void Move(int delta)
    {
        if (Pane == SettingsPane.Groups)
        {
            GroupIndex = Math.Clamp(GroupIndex + delta, 0, Groups.Count - 1);
            ListRow = 0;
            RightScroll = 0;
            return;
        }

        if (Group == SettingsGroup.Plugins)
        {
            ListRow = Math.Clamp(
                ListRow + delta,
                0,
                Math.Max(0, Snapshot.Plugins.Count - 1));
            ClearPluginPending();
            return;
        }
        if (Group == SettingsGroup.Config)
        {
            return;
        }
        if (View == SettingsView.Providers)
        {
            var maximum = Snapshot.Providers.Count + Snapshot.Models.Count - 1;
            ListRow = Math.Clamp(ListRow + delta, 0, Math.Max(0, maximum));
            return;
        }
        if (View == SettingsView.ProviderEditor)
        {
            ProviderEditorRowIndex = Math.Clamp(
                ProviderEditorRowIndex + delta,
                0,
                Math.Max(0, ProviderEditorRows.Count - 1));
            return;
        }
        ModelEditorRowIndex = Math.Clamp(ModelEditorRowIndex + delta, 0, 4);
    }

    private SettingsCommand Confirm()
    {
        if (Pane == SettingsPane.Groups)
        {
            Pane = SettingsPane.Content;
            return SettingsCommand.None;
        }
        if (Group == SettingsGroup.Plugins)
        {
            if (PendingPluginAction is { } action && SelectedPlugin is { } plugin)
            {
                return BuildPluginCommand(plugin, action);
            }
            Status = "select a plugin, then press e, d or r";
            return SettingsCommand.None;
        }
        if (Group != SettingsGroup.Model)
        {
            return SettingsCommand.None;
        }
        if (View == SettingsView.Providers)
        {
            if (SelectedModel is { } model)
            {
                return new SettingsCommand(
                    SettingsCommandKind.SetDefaultModel,
                    model.Provider,
                    model.Id);
            }
            OpenSelectedProvider();
            return SettingsCommand.None;
        }
        if (View == SettingsView.ProviderEditor)
        {
            var row = CurrentProviderRow;
            switch (row.Kind)
            {
                case ProviderRowKind.Enabled:
                    if (Draft is not null)
                    {
                        Draft.Enabled = !Draft.Enabled;
                    }
                    break;
                case ProviderRowKind.Mode:
                    if (Draft is not null)
                    {
                        if (Draft.AddressMode == SettingsAddressMode.Base)
                        {
                            var (chat, anthropic, responses) =
                                SettingsUrls.ExpandBase(Draft.BaseUrl.Value);
                            if (Draft.ChatUrl.Value.Length == 0)
                            {
                                Draft.ChatUrl.Set(chat);
                            }
                            if (Draft.AnthropicUrl.Value.Length == 0)
                            {
                                Draft.AnthropicUrl.Set(anthropic);
                            }
                            if (Draft.ResponsesUrl.Value.Length == 0)
                            {
                                Draft.ResponsesUrl.Set(responses);
                            }
                            Draft.AddressMode = SettingsAddressMode.Full;
                        }
                        else
                        {
                            Draft.AddressMode = SettingsAddressMode.Base;
                        }
                    }
                    break;
                case ProviderRowKind.ModelsHeader:
                case ProviderRowKind.AddModel:
                    if (row.Kind == ProviderRowKind.AddModel)
                    {
                        AddModel();
                    }
                    break;
                case ProviderRowKind.Model:
                    EditModel(row.ModelIndex);
                    break;
                default:
                    Move(1);
                    break;
            }
            return SettingsCommand.None;
        }

        if (ModelDraft is not null && ModelEditorRowIndex == (int)ModelRowKind.Protocol)
        {
            var protocols = SettingsModelDraft.Protocols;
            var current = protocols.ToList().IndexOf(ModelDraft.Protocol);
            ModelDraft.Protocol = protocols[(current + 1) % protocols.Count];
            return SettingsCommand.None;
        }
        Move(1);
        return SettingsCommand.None;
    }

    private SettingsCommand Back()
    {
        if (Group == SettingsGroup.Plugins && PendingPluginAction is not null)
        {
            ClearPluginPending();
            Status = null;
            return SettingsCommand.None;
        }
        if (View == SettingsView.ModelEditor)
        {
            View = SettingsView.ProviderEditor;
            ModelDraft = null;
            _modelEditIndex = -1;
            Error = null;
            return SettingsCommand.None;
        }
        if (View == SettingsView.ProviderEditor)
        {
            View = SettingsView.Providers;
            Draft = null;
            Error = null;
            return SettingsCommand.None;
        }
        if (Pane == SettingsPane.Content)
        {
            Pane = SettingsPane.Groups;
            return SettingsCommand.None;
        }
        return new SettingsCommand(SettingsCommandKind.Close);
    }

    private SettingsCommand RequestPluginAction(PluginAction action)
    {
        if (SelectedPlugin is not { } plugin)
        {
            Status = "no plugin selected";
            return SettingsCommand.None;
        }
        if (action == PluginAction.Enable && plugin.Enabled)
        {
            ClearPluginPending();
            Status = "plugin is already enabled";
            return SettingsCommand.None;
        }
        if (action == PluginAction.Disable && !plugin.Enabled)
        {
            ClearPluginPending();
            Status = "plugin is already disabled";
            return SettingsCommand.None;
        }
        if (PendingPluginAction == action && PendingPluginId == plugin.Id)
        {
            return BuildPluginCommand(plugin, action);
        }

        PendingPluginAction = action;
        PendingPluginId = plugin.Id;
        Error = null;
        var key = action switch
        {
            PluginAction.Enable => "e",
            PluginAction.Disable => "d",
            _ => "r"
        };
        Status =
            $"{action.ToString().ToLowerInvariant()} plugin {plugin.Id}? " +
            $"Press {key} again or Enter to confirm; Esc cancels.";
        return SettingsCommand.None;
    }

    private SettingsCommand BuildPluginCommand(SettingsPlugin plugin, PluginAction action)
    {
        ClearPluginPending();
        Error = null;
        Status = null;
        return action == PluginAction.Reload
            ? new SettingsCommand(SettingsCommandKind.ReloadPlugin, Plugin: plugin.Id)
            : new SettingsCommand(
                SettingsCommandKind.SetPluginEnabled,
                Enabled: action == PluginAction.Enable,
                Plugin: plugin.Id);
    }

    private void ClearPluginPending()
    {
        PendingPluginAction = null;
        PendingPluginId = null;
    }

    private bool SaveModelDraft()
    {
        if (Draft is null || ModelDraft is null)
        {
            return false;
        }
        var error = ModelDraft.Validate();
        if (error is not null)
        {
            Error = error;
            return false;
        }
        if (_modelEditIndex >= 0 && _modelEditIndex < Draft.Models.Count)
        {
            Draft.Models[_modelEditIndex] = ModelDraft;
        }
        else
        {
            Draft.Models.Add(ModelDraft);
        }
        ModelDraft = null;
        _modelEditIndex = -1;
        View = SettingsView.ProviderEditor;
        Error = null;
        Status = "model updated; saving provider...";
        return true;
    }

    private void OpenProviderEditorForModel(SettingsModel model)
    {
        var provider = Snapshot.Providers.FirstOrDefault(item => item.Id == model.Provider);
        if (provider is null)
        {
            return;
        }
        OpenProviderEditor(provider);
        var index = provider.Models
            .Select((item, modelIndex) => (item, modelIndex))
            .FirstOrDefault(item => item.item.Id == model.Id)
            .modelIndex;
        ProviderEditorRowIndex = Math.Max(0, ProviderEditorRows.Count - 2);
        for (var row = 0; row < ProviderEditorRows.Count; row++)
        {
            if (ProviderEditorRows[row].Kind == ProviderRowKind.Model
                && ProviderEditorRows[row].ModelIndex == index)
            {
                ProviderEditorRowIndex = row;
                break;
            }
        }
    }

    private void AddModelToSelectedProvider()
    {
        if (SelectedProvider is { } provider)
        {
            OpenProviderEditor(provider);
            if (View == SettingsView.ProviderEditor)
            {
                AddModel();
            }
        }
        else
        {
            OpenNewProvider();
            AddModel();
        }
    }

    private void EditInsert(char character) =>
        Edit(field => field.Insert(character));

    private SettingsText? CurrentTextField
    {
        get
        {
            if (View == SettingsView.ProviderEditor && Draft is not null)
            {
                var row = CurrentProviderRow;
                if (row.Kind == ProviderRowKind.Id && !Draft.IsNew)
                {
                    return null;
                }
                return GetProviderText(row.Kind);
            }
            return View == SettingsView.ModelEditor
                ? GetModelText(ModelEditorRowIndex)
                : null;
        }
    }

    private void Edit(Action<SettingsText> edit)
    {
        if (CurrentTextField is { } field)
        {
            edit(field);
        }
    }

    private SettingsText? GetProviderText(ProviderRowKind kind) => Draft is null
        ? null
        : kind switch
        {
            ProviderRowKind.Id => Draft.Id,
            ProviderRowKind.BaseUrl => Draft.BaseUrl,
            ProviderRowKind.ChatUrl => Draft.ChatUrl,
            ProviderRowKind.AnthropicUrl => Draft.AnthropicUrl,
            ProviderRowKind.ResponsesUrl => Draft.ResponsesUrl,
            ProviderRowKind.ApiKey => Draft.ApiKey,
            _ => null
        };

    private SettingsText? GetModelText(int row) => ModelDraft is null
        ? null
        : (ModelRowKind)row switch
        {
            ModelRowKind.Id => ModelDraft.Id,
            ModelRowKind.ContextWindow => ModelDraft.ContextWindow,
            ModelRowKind.CompactThreshold => ModelDraft.CompactThreshold,
            ModelRowKind.MaxOutputTokens => ModelDraft.MaxOutputTokens,
            _ => null
        };
}

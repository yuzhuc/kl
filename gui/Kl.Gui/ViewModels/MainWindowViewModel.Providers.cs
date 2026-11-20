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

public sealed partial class MainWindowViewModel : ObservableObject
{
    public ObservableCollection<ProviderCardRow> ProviderCards { get; } = [];

    public bool HasProviderCards => ProviderCards.Count > 0;

    public bool ShowNoProviderCards => !HasProviderCards;

    public string ProviderSummaryText => ProviderCards.Count == 0
        ? "尚未接入模型提供商。"
        : $"已接入 {ProviderCards.Count} 个提供商 · 支持同时启用多个模型提供商";

    private string _providerStatusText = string.Empty;
    public string ProviderStatusText
    {
        get => _providerStatusText;
        private set
        {
            if (SetProperty(ref _providerStatusText, value))
            {
                OnPropertyChanged(nameof(HasProviderStatus));
            }
        }
    }

    public bool HasProviderStatus => ProviderStatusText.Length > 0;

    private bool _isProviderFormOpen;
    public bool IsProviderFormOpen
    {
        get => _isProviderFormOpen;
        private set => SetProperty(ref _isProviderFormOpen, value);
    }

    private string _providerDraftName = string.Empty;
    public string ProviderDraftName
    {
        get => _providerDraftName;
        set => SetProperty(ref _providerDraftName, value);
    }

    private string _providerDraftNote = string.Empty;
    public string ProviderDraftNote
    {
        get => _providerDraftNote;
        set => SetProperty(ref _providerDraftNote, value);
    }

    private string _providerDraftWebsite = string.Empty;
    public string ProviderDraftWebsite
    {
        get => _providerDraftWebsite;
        set => SetProperty(ref _providerDraftWebsite, value);
    }

    private string _providerDraftChatUrl = string.Empty;
    public string ProviderDraftChatUrl
    {
        get => _providerDraftChatUrl;
        set => SetProperty(ref _providerDraftChatUrl, value);
    }

    private string _providerDraftAnthropicUrl = string.Empty;
    public string ProviderDraftAnthropicUrl
    {
        get => _providerDraftAnthropicUrl;
        set => SetProperty(ref _providerDraftAnthropicUrl, value);
    }

    private string _providerDraftResponsesUrl = string.Empty;
    public string ProviderDraftResponsesUrl
    {
        get => _providerDraftResponsesUrl;
        set => SetProperty(ref _providerDraftResponsesUrl, value);
    }

    private string _providerDraftApiKey = string.Empty;
    public string ProviderDraftApiKey
    {
        get => _providerDraftApiKey;
        set => SetProperty(ref _providerDraftApiKey, value);
    }

    private int _providerChatSuffixIndex = 1;
    public int ProviderChatSuffixIndex
    {
        get => _providerChatSuffixIndex;
        set => SetProperty(ref _providerChatSuffixIndex, value);
    }

    private int _providerAnthropicSuffixIndex = 1;
    public int ProviderAnthropicSuffixIndex
    {
        get => _providerAnthropicSuffixIndex;
        set => SetProperty(ref _providerAnthropicSuffixIndex, value);
    }

    private int _providerResponsesSuffixIndex = 1;
    public int ProviderResponsesSuffixIndex
    {
        get => _providerResponsesSuffixIndex;
        set => SetProperty(ref _providerResponsesSuffixIndex, value);
    }

    private bool _providerChatProtocolEnabled = true;
    public bool ProviderChatProtocolEnabled
    {
        get => _providerChatProtocolEnabled;
        set => SetProperty(ref _providerChatProtocolEnabled, value);
    }

    private bool _providerAnthropicProtocolEnabled;
    public bool ProviderAnthropicProtocolEnabled
    {
        get => _providerAnthropicProtocolEnabled;
        set => SetProperty(ref _providerAnthropicProtocolEnabled, value);
    }

    private bool _providerResponsesProtocolEnabled;
    public bool ProviderResponsesProtocolEnabled
    {
        get => _providerResponsesProtocolEnabled;
        set => SetProperty(ref _providerResponsesProtocolEnabled, value);
    }

    private string _providerFormStatus = string.Empty;
    public string ProviderFormStatus
    {
        get => _providerFormStatus;
        private set
        {
            if (SetProperty(ref _providerFormStatus, value))
            {
                OnPropertyChanged(nameof(HasProviderFormStatus));
            }
        }
    }

    public bool HasProviderFormStatus => ProviderFormStatus.Length > 0;

    private bool _isProviderDetailOpen;
    public bool IsProviderDetailOpen
    {
        get => _isProviderDetailOpen;
        private set
        {
            if (SetProperty(ref _isProviderDetailOpen, value))
            {
                OnPropertyChanged(nameof(IsProviderListOpen));
            }
        }
    }

    public bool IsProviderListOpen => !IsProviderDetailOpen;

    private ProviderCardRow? _detailProvider;
    public string ProviderDetailName => _detailProvider?.Name ?? string.Empty;
    public string ProviderDetailId => _detailProvider?.Id ?? string.Empty;
    public string ProviderDetailNote => _detailProvider?.Note ?? string.Empty;
    public string ProviderDetailWebsite => _detailProvider?.Website ?? string.Empty;
    public bool HasProviderDetailNote => !string.IsNullOrWhiteSpace(ProviderDetailNote);
    public bool HasProviderDetailWebsite => !string.IsNullOrWhiteSpace(ProviderDetailWebsite);
    public Avalonia.Layout.Orientation ProviderDetailHeaderOrientation =>
        HasProviderDetailWebsite
            ? Avalonia.Layout.Orientation.Horizontal
            : Avalonia.Layout.Orientation.Vertical;
    public string ProviderDetailKeyStatus =>
        _detailProvider?.KeyConfigured == true ? "API Key 已配置" : "API Key 未配置";

    private string _providerDetailStatus = string.Empty;
    public string ProviderDetailStatus
    {
        get => _providerDetailStatus;
        private set
        {
            if (SetProperty(ref _providerDetailStatus, value))
            {
                OnPropertyChanged(nameof(HasProviderDetailStatus));
            }
        }
    }

    public bool HasProviderDetailStatus => ProviderDetailStatus.Length > 0;

    private string _providerConnectionStatus = string.Empty;
    public string ProviderConnectionStatus
    {
        get => _providerConnectionStatus;
        private set
        {
            if (SetProperty(ref _providerConnectionStatus, value))
            {
                OnPropertyChanged(nameof(HasProviderConnectionStatus));
            }
        }
    }

    public bool HasProviderConnectionStatus => ProviderConnectionStatus.Length > 0;

    private bool _isProviderDeleteConfirmOpen;
    public bool IsProviderDeleteConfirmOpen
    {
        get => _isProviderDeleteConfirmOpen;
        private set => SetProperty(ref _isProviderDeleteConfirmOpen, value);
    }

    public ObservableCollection<ProviderModelRow> ProviderModels { get; } = [];
    public bool HasProviderModels => ProviderModels.Count > 0;
    public bool ShowNoProviderModels => !HasProviderModels;

    private string _editingProviderId = string.Empty;

    private string _providerEditName = string.Empty;
    public string ProviderEditName
    {
        get => _providerEditName;
        set => SetProperty(ref _providerEditName, value);
    }

    private string _providerEditNote = string.Empty;
    public string ProviderEditNote
    {
        get => _providerEditNote;
        set => SetProperty(ref _providerEditNote, value);
    }

    private string _providerEditWebsite = string.Empty;
    public string ProviderEditWebsite
    {
        get => _providerEditWebsite;
        set => SetProperty(ref _providerEditWebsite, value);
    }

    private string _providerEditStatus = string.Empty;
    public string ProviderEditStatus
    {
        get => _providerEditStatus;
        private set
        {
            if (SetProperty(ref _providerEditStatus, value))
            {
                OnPropertyChanged(nameof(HasProviderEditStatus));
            }
        }
    }

    public bool HasProviderEditStatus => ProviderEditStatus.Length > 0;

    public void OpenProviderDetail(ProviderCardRow provider)
    {
        _detailProvider = provider;
        _editingProviderId = provider.Id.ToLowerInvariant();
        ProviderEditName = provider.Name;
        ProviderEditNote = provider.Note;
        ProviderEditWebsite = provider.Website;
        ProviderEditStatus = string.Empty;
        ProviderDetailStatus = string.Empty;
        ProviderDetailApiKey = string.Empty;
        IsProviderDeleteConfirmOpen = false;
        ProviderModels.Clear();
        foreach (var model in provider.Models)
        {
            ProviderModels.Add(new ProviderModelRow(
                model.Model,
                NormalizeModelProtocol(model.Protocol),
                model.Url,
                model.ContextWindow,
                model.CompactThreshold,
                model.MaxOutputTokens,
                model.Enabled,
                model.ReasoningEffort,
                model.ReasoningEfforts.Count > 0 ? model.ReasoningEfforts : DefaultReasoningLevels,
                model.Name));
        }

        LoadProviderConnections(provider);
        CloseProviderModelForm();
        IsProviderFormOpen = false;
        NotifyProviderModelsChanged();
        NotifyProviderDetailChanged();
        IsProviderDetailOpen = true;
    }

    public void CloseProviderDetail()
    {
        IsProviderDetailOpen = false;
        _detailProvider = null;
        _editingProviderId = string.Empty;
        ProviderEditStatus = string.Empty;
        ProviderDetailStatus = string.Empty;
        ProviderConnectionStatus = string.Empty;
        ProviderDetailApiKey = string.Empty;
        IsProviderDeleteConfirmOpen = false;
        ProviderModels.Clear();
        CloseProviderModelForm();
        NotifyProviderModelsChanged();
        NotifyProviderDetailChanged();
    }

    /// <summary>Fills the detail form with each protocol's complete request URL.</summary>
    private void LoadProviderConnections(ProviderCardRow provider)
    {
        string Find(string protocol)
        {
            if (provider.Protocols.TryGetValue(protocol, out var url)
                && !string.IsNullOrWhiteSpace(url))
            {
                return url.Trim();
            }

            return provider.Models.FirstOrDefault(model =>
                string.Equals(model.Protocol, protocol, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(model.Url))?.Url ?? string.Empty;
        }

        var chat = Find("openai-chat");
        var anthropic = Find("anthropic");
        var responses = Find("openai-responses");

        // 详情页直接编辑完整地址；suffix 置 0 让保存时原样写入。
        ProviderDraftChatUrl = chat;
        ProviderChatSuffixIndex = 0;
        ProviderChatProtocolEnabled = chat.Length > 0;

        ProviderDraftAnthropicUrl = anthropic;
        ProviderAnthropicSuffixIndex = 0;
        ProviderAnthropicProtocolEnabled = anthropic.Length > 0;

        ProviderDraftResponsesUrl = responses;
        ProviderResponsesSuffixIndex = 0;
        ProviderResponsesProtocolEnabled = responses.Length > 0;

        ProviderConnectionStatus = string.Empty;
    }

    /// <summary>Writes the detail page's protocol URLs without changing its models.</summary>
    public async Task SaveProviderConnectionAsync()
    {
        var provider = _detailProvider;
        if (provider is null)
        {
            ProviderConnectionStatus = "请先打开一个提供商。";
            return;
        }

        var models = ProviderModels.ToList();
        var protocols = new Dictionary<string, string?>();
        bool AddProtocol(string key, string url, bool enabled, string label)
        {
            if (!enabled)
            {
                return true;
            }

            url = url.Trim();
            if (url.Length == 0)
            {
                ProviderConnectionStatus = $"请填写 {label} 的请求地址。";
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                ProviderConnectionStatus = $"{label} 的请求地址必须是完整的 http:// 或 https:// 地址。";
                return false;
            }

            protocols[key] = url;
            return true;
        }

        if (!AddProtocol("openai-chat", ProviderChatEndpoint(), ProviderChatProtocolEnabled, "OpenAI Chat")
            || !AddProtocol("anthropic", ProviderAnthropicEndpoint(), ProviderAnthropicProtocolEnabled, "Anthropic")
            || !AddProtocol("openai-responses", ProviderResponsesEndpoint(), ProviderResponsesProtocolEnabled, "OpenAI Responses"))
        {
            return;
        }
        if (protocols.Count == 0)
        {
            ProviderConnectionStatus = "请至少启用一种协议。";
            return;
        }

        var parameters = new Dictionary<string, object?>
        {
            ["id"] = provider.Id,
            ["enabled"] = provider.Enabled,
            ["protocols"] = protocols,
            ["models"] = models.Select(model => new Dictionary<string, object?>
            {
                ["id"] = model.Id,
                ["name"] = model.Name,
                ["protocol"] = model.Protocol,
                ["context_window"] = model.ContextWindow,
                ["compact_threshold"] = model.CompactThreshold,
                ["max_output_tokens"] = model.MaxOutputTokens,
                ["url"] = model.Url,
                ["enabled"] = model.Enabled,
                ["reasoning_effort"] = model.ReasoningEffort,
                ["reasoning_efforts"] = model.ReasoningEfforts
            }).ToArray()
        };

        try
        {
            await _core.UpsertProviderAsync(parameters, CancellationToken.None).ConfigureAwait(true);
            provider.SetProtocols(SavedProtocols(protocols));
            SaveProviderCache();
            if (ReferenceEquals(_detailProvider, provider))
            {
                ProviderConnectionStatus = "连接配置已写入 KL Core；重新连接后应用到聊天。";
            }
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            if (ReferenceEquals(_detailProvider, provider))
            {
                ProviderConnectionStatus = $"保存连接配置失败：{error.Message}";
            }
        }
    }

    public void OpenProviderDeleteConfirm()
    {
        if (_detailProvider is null)
        {
            return;
        }

        ProviderDetailStatus = string.Empty;
        IsProviderDeleteConfirmOpen = true;
    }

    public void CancelProviderDeleteConfirm()
    {
        IsProviderDeleteConfirmOpen = false;
        ProviderDetailStatus = string.Empty;
    }

    /// <summary>Removes the provider from the UI immediately, then confirms with Core.</summary>
    public async Task ConfirmDeleteProviderAsync()
    {
        if (_detailProvider is null || !IsProviderDeleteConfirmOpen)
        {
            return;
        }

        var id = _detailProvider.Id;
        var rows = ProviderCards
            .Where(card => string.Equals(card.Id, id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var row = rows.FirstOrDefault();
        var index = row is null ? -1 : ProviderCards.IndexOf(row);

        IsProviderDeleteConfirmOpen = false;
        foreach (var duplicate in rows)
        {
            ProviderCards.Remove(duplicate);
        }

        if (rows.Count > 0)
        {
            NotifyProviderCardsChanged();
        }

        CloseProviderDetail();
        ProviderStatusText = string.Empty;

        try
        {
            await _core.DeleteProviderAsync(id, CancellationToken.None).ConfigureAwait(true);
            var profiles = _state.ProviderProfiles;
            if (profiles is not null && profiles.Remove(id.ToLowerInvariant()))
            {
                SaveState();
            }

            SaveProviderCache();
            await RefreshProvidersAsync(force: true, announce: false).ConfigureAwait(true);
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or OperationCanceledException
            or TimeoutException)
        {
            if (row is not null
                && !ProviderCards.Any(card =>
                    string.Equals(card.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                ProviderCards.Insert(Math.Clamp(index, 0, ProviderCards.Count), row);
                NotifyProviderCardsChanged();
            }

            ProviderStatusText = $"无法删除提供商 {id}：{error.Message}";
        }
    }

    private void NotifyProviderDetailChanged()
    {
        OnPropertyChanged(nameof(ProviderDetailName));
        OnPropertyChanged(nameof(ProviderDetailNote));
        OnPropertyChanged(nameof(ProviderDetailWebsite));
        OnPropertyChanged(nameof(HasProviderDetailNote));
        OnPropertyChanged(nameof(HasProviderDetailWebsite));
        OnPropertyChanged(nameof(ProviderDetailHeaderOrientation));
        OnPropertyChanged(nameof(ProviderDetailKeyStatus));
    }

    private void NotifyProviderModelsChanged()
    {
        OnPropertyChanged(nameof(HasProviderModels));
        OnPropertyChanged(nameof(ShowNoProviderModels));
    }

    public bool SaveProviderProfile()
    {
        var name = ProviderEditName.Trim();
        if (name.Length == 0)
        {
            ProviderEditStatus = "请填写供应商名称。";
            return false;
        }

        var website = ProviderEditWebsite.Trim();
        if (website.Length > 0
            && (!Uri.TryCreate(website, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            ProviderEditStatus = "官网链接必须是完整的 http:// 或 https:// 地址。";
            return false;
        }

        var note = ProviderEditNote.Trim();
        var profiles = _state.ProviderProfiles ??= [];
        profiles[_editingProviderId] = new ProviderProfileState
        {
            DisplayName = name,
            Note = note,
            Website = website
        };
        SaveState();

        if (ProviderCards.FirstOrDefault(row =>
                string.Equals(row.Id, _editingProviderId, StringComparison.OrdinalIgnoreCase)) is { } row)
        {
            row.Name = name;
            row.Note = note;
            row.Website = website;
        }

        if (_detailProvider is not null)
        {
            _detailProvider.Name = name;
            _detailProvider.Note = note;
            _detailProvider.Website = website;
        }

        ProviderEditStatus = string.Empty;
        ProviderDetailStatus = $"已保存 {name} 的显示信息。";
        NotifyProviderDetailChanged();
        return true;
    }

    // ----- Provider model management -----

    private ProviderModelRow? _editingProviderModel;
    private bool _isProviderModelFormOpen;
    public bool IsProviderModelFormOpen
    {
        get => _isProviderModelFormOpen;
        private set => SetProperty(ref _isProviderModelFormOpen, value);
    }

    public string ProviderModelFormTitle =>
        _editingProviderModel is null ? "添加模型" : "编辑模型";

    private string _providerModelDraftName = string.Empty;
    public string ProviderModelDraftName
    {
        get => _providerModelDraftName;
        set => SetProperty(ref _providerModelDraftName, value);
    }

    private string _providerModelDraftId = string.Empty;
    public string ProviderModelDraftId
    {
        get => _providerModelDraftId;
        set => SetProperty(ref _providerModelDraftId, value);
    }

    private int _providerModelProtocolIndex;
    private int _providerModelCustomProtocolIndex;
    public int ProviderModelProtocolIndex
    {
        get => _providerModelProtocolIndex;
        set
        {
            if (!SetProperty(ref _providerModelProtocolIndex, value))
            {
                return;
            }

            if (value is >= 0 and <= 2)
            {
                _providerModelCustomProtocolIndex = value;
                var url = ProviderProtocolUrl(value);
                if (url.Length > 0)
                {
                    ProviderModelDraftUrl = url;
                }
            }
        }
    }

    public ObservableCollection<ReasoningLevelChoice> ProviderModelReasoningLevels { get; } = [];
    public ObservableCollection<string> ProviderModelDefaultReasoningChoices { get; } = [];

    private string _selectedProviderModelDefaultReasoning = string.Empty;
    public string SelectedProviderModelDefaultReasoning
    {
        get => _selectedProviderModelDefaultReasoning;
        set => SetProperty(ref _selectedProviderModelDefaultReasoning, value);
    }

    private static readonly string[] ReasoningLevels =
        ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];
    private static readonly string[] DefaultReasoningLevels = ["low", "medium", "high", "max"];

    private string _providerModelDraftUrl = string.Empty;
    public string ProviderModelDraftUrl
    {
        get => _providerModelDraftUrl;
        set => SetProperty(ref _providerModelDraftUrl, value);
    }

    private string _providerModelDraftContextWindow = "128000";
    public string ProviderModelDraftContextWindow
    {
        get => _providerModelDraftContextWindow;
        set => SetProperty(ref _providerModelDraftContextWindow, value);
    }

    private string _providerModelDraftCompactThreshold = "0.8";
    public string ProviderModelDraftCompactThreshold
    {
        get => _providerModelDraftCompactThreshold;
        set => SetProperty(ref _providerModelDraftCompactThreshold, value);
    }

    private string _providerModelDraftMaxOutput = "8192";
    public string ProviderModelDraftMaxOutput
    {
        get => _providerModelDraftMaxOutput;
        set => SetProperty(ref _providerModelDraftMaxOutput, value);
    }

    private string _providerModelFormStatus = string.Empty;
    public string ProviderModelFormStatus
    {
        get => _providerModelFormStatus;
        private set
        {
            if (SetProperty(ref _providerModelFormStatus, value))
            {
                OnPropertyChanged(nameof(HasProviderModelFormStatus));
            }
        }
    }

    public bool HasProviderModelFormStatus => ProviderModelFormStatus.Length > 0;

    public ObservableCollection<string> ProviderFetchedModels { get; } = [];
    public bool HasProviderFetchedModels => ProviderFetchedModels.Count > 0;

    private string _selectedProviderFetchedModel = string.Empty;
    public string SelectedProviderFetchedModel
    {
        get => _selectedProviderFetchedModel;
        set
        {
            if (SetProperty(ref _selectedProviderFetchedModel, value)
                && !string.IsNullOrWhiteSpace(value))
            {
                ProviderModelDraftId = value;
            }
        }
    }

    private bool _isFetchingProviderModels;
    public bool IsFetchingProviderModels
    {
        get => _isFetchingProviderModels;
        private set
        {
            if (SetProperty(ref _isFetchingProviderModels, value))
            {
                OnPropertyChanged(nameof(CanFetchProviderModels));
            }
        }
    }

    public bool CanFetchProviderModels => !IsFetchingProviderModels;

    private string _providerModelFetchStatus = string.Empty;
    public string ProviderModelFetchStatus
    {
        get => _providerModelFetchStatus;
        private set
        {
            if (SetProperty(ref _providerModelFetchStatus, value))
            {
                OnPropertyChanged(nameof(HasProviderModelFetchStatus));
            }
        }
    }

    public bool HasProviderModelFetchStatus => ProviderModelFetchStatus.Length > 0;

    public async Task FetchProviderModelsAsync()
    {
        if (_detailProvider is null || IsFetchingProviderModels)
        {
            return;
        }

        IsFetchingProviderModels = true;
        ProviderModelFetchStatus = "正在从 /models 获取模型…";
        try
        {
            var result = await _core.FetchProviderModelsAsync(
                _detailProvider.Id,
                CancellationToken.None).ConfigureAwait(true);
            var models = new List<string>();
            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("models", out var values)
                && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.String
                        && value.GetString() is { Length: > 0 } model
                        && !models.Contains(model, StringComparer.OrdinalIgnoreCase))
                    {
                        models.Add(model);
                    }
                }
            }

            ProviderFetchedModels.Clear();
            foreach (var model in models)
            {
                ProviderFetchedModels.Add(model);
            }
            OnPropertyChanged(nameof(HasProviderFetchedModels));

            if (models.Count == 0)
            {
                ProviderModelFetchStatus = "提供商没有返回模型。";
                return;
            }

            if (result.TryGetProperty("protocol", out var protocol)
                && protocol.ValueKind == JsonValueKind.String
                && _editingProviderModel is null)
            {
                ProviderModelProtocolIndex = protocol.GetString() switch
                {
                    "anthropic" => 1,
                    "openai-responses" => 2,
                    _ => 0
                };
                if (result.TryGetProperty("url", out var url)
                    && url.ValueKind == JsonValueKind.String)
                {
                    ProviderModelDraftUrl = url.GetString() ?? string.Empty;
                }
            }

            SelectedProviderFetchedModel = ProviderFetchedModels[0];
            ProviderModelFetchStatus =
                $"已获取 {models.Count} 个模型；选择后自动填入模型 ID。";
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            ProviderModelFetchStatus = $"获取模型列表失败：{error.Message}";
        }
        finally
        {
            IsFetchingProviderModels = false;
        }
    }

    private void ClearProviderFetchedModels()
    {
        ProviderFetchedModels.Clear();
        _selectedProviderFetchedModel = string.Empty;
        OnPropertyChanged(nameof(SelectedProviderFetchedModel));
        OnPropertyChanged(nameof(HasProviderFetchedModels));
        ProviderModelFetchStatus = string.Empty;
    }

    private void ResetProviderModelReasoningLevels(
        IEnumerable<string> enabledLevels,
        string defaultLevel)
    {
        var enabled = new HashSet<string>(enabledLevels, StringComparer.OrdinalIgnoreCase);
        ProviderModelReasoningLevels.Clear();
        foreach (var level in ReasoningLevels)
        {
            ProviderModelReasoningLevels.Add(new ReasoningLevelChoice(
                level,
                level,
                enabled.Contains(level),
                RefreshProviderModelReasoningChoices));
        }

        ProviderModelDefaultReasoningChoices.Clear();
        foreach (var level in ReasoningLevels.Where(enabled.Contains))
        {
            ProviderModelDefaultReasoningChoices.Add(level);
        }

        SelectedProviderModelDefaultReasoning = ResolveReasoningDefault(defaultLevel);
    }

    private void RefreshProviderModelReasoningChoices()
    {
        var current = SelectedProviderModelDefaultReasoning;
        var enabled = ProviderModelReasoningLevels
            .Where(level => level.IsEnabled)
            .Select(level => level.Value)
            .ToList();

        // Avoid Clear(): clearing the bound collection also clears the ComboBox
        // selection. Adding a level must leave the current default untouched.
        for (var index = ProviderModelDefaultReasoningChoices.Count - 1; index >= 0; index--)
        {
            if (!enabled.Contains(
                    ProviderModelDefaultReasoningChoices[index],
                    StringComparer.OrdinalIgnoreCase))
            {
                ProviderModelDefaultReasoningChoices.RemoveAt(index);
            }
        }

        for (var index = 0; index < enabled.Count; index++)
        {
            var existing = IndexOfReasoningChoice(enabled[index]);
            if (existing == index)
            {
                continue;
            }

            if (existing >= 0)
            {
                ProviderModelDefaultReasoningChoices.Move(existing, index);
            }
            else
            {
                ProviderModelDefaultReasoningChoices.Insert(index, enabled[index]);
            }
        }

        if (enabled.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        SelectedProviderModelDefaultReasoning = NextReasoningDefault(current, enabled);
    }

    private string ResolveReasoningDefault(string current)
    {
        if (ProviderModelDefaultReasoningChoices.Contains(
                current,
                StringComparer.OrdinalIgnoreCase))
        {
            return current;
        }

        if (ProviderModelDefaultReasoningChoices.Contains("medium", StringComparer.OrdinalIgnoreCase))
        {
            return "medium";
        }

        return ProviderModelDefaultReasoningChoices.FirstOrDefault() ?? string.Empty;
    }

    private int IndexOfReasoningChoice(string level)
    {
        for (var index = 0; index < ProviderModelDefaultReasoningChoices.Count; index++)
        {
            if (string.Equals(
                    ProviderModelDefaultReasoningChoices[index],
                    level,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string NextReasoningDefault(
        string current,
        IReadOnlyList<string> enabled)
    {
        if (enabled.Count == 0)
        {
            return string.Empty;
        }

        var index = Array.FindIndex(
            ReasoningLevels,
            level => string.Equals(level, current, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            for (var next = index + 1; next < ReasoningLevels.Length; next++)
            {
                if (enabled.Contains(ReasoningLevels[next], StringComparer.OrdinalIgnoreCase))
                {
                    return ReasoningLevels[next];
                }
            }
        }

        if (string.IsNullOrWhiteSpace(current)
            && enabled.Contains("medium", StringComparer.OrdinalIgnoreCase))
        {
            return "medium";
        }

        return enabled[^1];
    }

    public void OpenAddProviderModel()
    {
        _editingProviderModel = null;
        ProviderModelDraftName = string.Empty;
        ProviderModelDraftId = string.Empty;
        ProviderModelDraftUrl = string.Empty;
        ProviderModelDraftContextWindow = "128000";
        ProviderModelDraftCompactThreshold = "0.8";
        ProviderModelDraftMaxOutput = "8192";
        ProviderModelProtocolIndex = 0;
        ProviderModelDraftUrl = ProviderProtocolUrl(0);
        ResetProviderModelReasoningLevels(DefaultReasoningLevels, "medium");
        ProviderModelFormStatus = string.Empty;
        ClearProviderFetchedModels();
        OnPropertyChanged(nameof(ProviderModelFormTitle));
        IsProviderModelFormOpen = true;
    }

    public void OpenEditProviderModel(ProviderModelRow model)
    {
        _editingProviderModel = model;
        ProviderModelDraftName = model.Name;
        ProviderModelDraftId = model.Id;
        ProviderModelDraftUrl = model.Url;
        ProviderModelDraftContextWindow = model.ContextWindow.ToString(CultureInfo.InvariantCulture);
        ProviderModelDraftCompactThreshold =
            model.CompactThreshold.ToString("0.##", CultureInfo.InvariantCulture);
        ProviderModelDraftMaxOutput = model.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);
        var protocolIndex = model.Protocol switch
        {
            "anthropic" => 1,
            "openai-responses" => 2,
            _ => 0
        };
        var providerUrl = ProviderProtocolUrl(protocolIndex);
        var modelUrl = model.Url.Trim();
        var custom = modelUrl.Length > 0
            && providerUrl.Length > 0
            && !string.Equals(modelUrl, providerUrl, StringComparison.OrdinalIgnoreCase);
        _providerModelCustomProtocolIndex = protocolIndex;
        ProviderModelProtocolIndex = custom ? 3 : protocolIndex;
        ProviderModelDraftUrl = custom || modelUrl.Length > 0 ? modelUrl : providerUrl;
        var supportedLevels = model.ReasoningEfforts.Count > 0
            ? model.ReasoningEfforts
            : DefaultReasoningLevels;
        ResetProviderModelReasoningLevels(supportedLevels, model.ReasoningEffort);
        ProviderModelFormStatus = string.Empty;
        ClearProviderFetchedModels();
        OnPropertyChanged(nameof(ProviderModelFormTitle));
        IsProviderModelFormOpen = true;
    }

    public void CloseProviderModelForm()
    {
        IsProviderModelFormOpen = false;
        _editingProviderModel = null;
        ProviderModelFormStatus = string.Empty;
        ClearProviderFetchedModels();
    }

    public async Task SaveProviderModelAsync()
    {
        var id = ProviderModelDraftId.Trim();
        if (id.Length == 0)
        {
            ProviderModelFormStatus = "请填写模型 ID。";
            return;
        }

        var url = ProviderModelDraftUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ProviderModelFormStatus = "模型 URL 必须是完整的 http:// 或 https:// 地址。";
            return;
        }

        if (!int.TryParse(
                ProviderModelDraftContextWindow.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var contextWindow)
            || contextWindow <= 0)
        {
            ProviderModelFormStatus = "上下文窗口必须是正整数。";
            return;
        }

        if (!double.TryParse(
                ProviderModelDraftCompactThreshold.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var compactThreshold)
            || compactThreshold <= 0
            || compactThreshold > 1)
        {
            ProviderModelFormStatus = "压缩阈值必须在 0 到 1 之间。";
            return;
        }

        if (!int.TryParse(
                ProviderModelDraftMaxOutput.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var maxOutput)
            || maxOutput <= 0)
        {
            ProviderModelFormStatus = "最大输出 tokens 必须是正整数。";
            return;
        }

        var duplicate = ProviderModels.Any(model =>
            !ReferenceEquals(model, _editingProviderModel)
            && string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            ProviderModelFormStatus = "这个提供商下已经有同名模型。";
            return;
        }

        var protocolIndex = ProviderModelProtocolIndex == 3
            ? _providerModelCustomProtocolIndex
            : ProviderModelProtocolIndex;
        var supportedLevels = ProviderModelReasoningLevels
            .Where(level => level.IsEnabled)
            .Select(level => level.Value)
            .ToList();
        if (supportedLevels.Count == 0)
        {
            ProviderModelFormStatus = "请至少启用一个思考等级。";
            return;
        }

        var defaultReasoning = ProviderModelDefaultReasoningChoices.Contains(
                SelectedProviderModelDefaultReasoning,
                StringComparer.OrdinalIgnoreCase)
            ? SelectedProviderModelDefaultReasoning
            : supportedLevels[0];
        var provider = _detailProvider;
        var previous = _editingProviderModel;
        var previousIndex = previous is null ? -1 : ProviderModels.IndexOf(previous);
        var row = new ProviderModelRow(
            id,
            ProviderProtocol(protocolIndex),
            url,
            contextWindow,
            compactThreshold,
            maxOutput,
            previous?.Enabled ?? true,
            defaultReasoning,
            supportedLevels,
            ProviderModelDraftName.Trim());
        if (previousIndex >= 0)
        {
            ProviderModels[previousIndex] = row;
        }
        else
        {
            ProviderModels.Add(row);
        }

        NotifyProviderModelsChanged();
        CloseProviderModelForm();
        if (!await SaveProviderModelsAsync().ConfigureAwait(true)
            && ReferenceEquals(_detailProvider, provider))
        {
            var rowIndex = ProviderModels.IndexOf(row);
            if (rowIndex >= 0)
            {
                if (previous is null)
                {
                    ProviderModels.RemoveAt(rowIndex);
                }
                else
                {
                    ProviderModels[rowIndex] = previous;
                }
            }

            NotifyProviderModelsChanged();
        }
    }

    public async Task DeleteProviderModelAsync(ProviderModelRow model)
    {
        var index = ProviderModels.IndexOf(model);
        if (index < 0)
        {
            return;
        }

        ProviderModels.RemoveAt(index);
        NotifyProviderModelsChanged();
        if (!await SaveProviderModelsAsync().ConfigureAwait(true))
        {
            ProviderModels.Insert(index, model);
            NotifyProviderModelsChanged();
        }
    }

    public async Task SetProviderModelEnabledAsync(ProviderModelRow model, bool enabled)
    {
        model.Enabled = enabled;
        if (!await SaveProviderModelsAsync().ConfigureAwait(true))
        {
            model.Enabled = !enabled;
        }
    }

    private async Task<bool> SaveProviderModelsAsync()
    {
        var provider = _detailProvider;
        if (provider is null)
        {
            ProviderDetailStatus = "请先打开一个提供商。";
            return false;
        }

        var models = ProviderModels.ToList();
        // Model edits must not remove protocols saved from the connection form.
        var protocols = provider.Protocols
            .ToDictionary(
                pair => pair.Key,
                pair => (string?)pair.Value,
                StringComparer.OrdinalIgnoreCase);
        void AddEnabled(string key, string url, bool enabled)
        {
            if (enabled && !string.IsNullOrWhiteSpace(url))
            {
                protocols[key] = url.Trim();
            }
        }

        AddEnabled("openai-chat", ProviderDraftChatUrl, ProviderChatProtocolEnabled);
        AddEnabled("anthropic", ProviderDraftAnthropicUrl, ProviderAnthropicProtocolEnabled);
        AddEnabled("openai-responses", ProviderDraftResponsesUrl, ProviderResponsesProtocolEnabled);
        foreach (var model in models)
        {
            if (!string.IsNullOrWhiteSpace(model.Url)
                && !protocols.ContainsKey(model.Protocol))
            {
                protocols[model.Protocol] = model.Url;
            }
        }
        var parameters = new Dictionary<string, object?>
        {
            ["id"] = provider.Id,
            ["enabled"] = provider.Enabled,
            ["protocols"] = protocols,
            ["models"] = models.Select(model => new Dictionary<string, object?>
            {
                ["id"] = model.Id,
                ["name"] = model.Name,
                ["protocol"] = model.Protocol,
                ["context_window"] = model.ContextWindow,
                ["compact_threshold"] = model.CompactThreshold,
                ["max_output_tokens"] = model.MaxOutputTokens,
                ["url"] = model.Url,
                ["enabled"] = model.Enabled,
                ["reasoning_effort"] = model.ReasoningEffort,
                ["reasoning_efforts"] = model.ReasoningEfforts
            }).ToArray()
        };

        try
        {
            await _core.UpsertProviderAsync(parameters, CancellationToken.None).ConfigureAwait(true);
            provider.SetProtocols(SavedProtocols(protocols));
            provider.SetModels(models
                .Select(model => new CoreModelOption(
                    provider.Id,
                    model.Id,
                    model.Enabled,
                    provider.KeyConfigured)
                {
                    Protocol = model.Protocol,
                    Url = model.Url,
                    ContextWindow = model.ContextWindow,
                    CompactThreshold = model.CompactThreshold,
                    MaxOutputTokens = model.MaxOutputTokens,
                    Name = model.Name,
                    ReasoningEffort = model.ReasoningEffort,
                    ReasoningEfforts = model.ReasoningEfforts
                })
                .ToList());
            SaveProviderCache();
            return true;
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            if (ReferenceEquals(_detailProvider, provider))
            {
                ProviderDetailStatus = $"保存模型配置失败：{error.Message}";
            }

            return false;
        }
    }

    private string _providerDetailApiKey = string.Empty;
    public string ProviderDetailApiKey
    {
        get => _providerDetailApiKey;
        set => SetProperty(ref _providerDetailApiKey, value);
    }

    public async Task SaveProviderDetailApiKeyAsync()
    {
        var provider = _detailProvider;
        if (provider is null)
        {
            return;
        }

        var key = ProviderDetailApiKey.Trim();
        if (key.Length == 0)
        {
            ProviderDetailStatus = "请先输入 API Key。";
            return;
        }

        try
        {
            await _core.SetProviderApiKeyAsync(provider.Id, key, CancellationToken.None)
                .ConfigureAwait(true);
            provider.SetKeyConfigured(true);
            if (ReferenceEquals(_detailProvider, provider))
            {
                ProviderDetailApiKey = string.Empty;
                OnPropertyChanged(nameof(ProviderDetailKeyStatus));
            }
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            if (ReferenceEquals(_detailProvider, provider))
            {
                ProviderDetailStatus = $"保存 API Key 失败：{error.Message}";
            }
        }
    }

    private static string NormalizeModelProtocol(string protocol) => protocol.ToLowerInvariant() switch
    {
        "anthropic" => "anthropic",
        "openai-responses" => "openai-responses",
        _ => "openai-chat"
    };

    private readonly HashSet<string> _providerTogglesInFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ProviderRefreshCache = TimeSpan.FromMinutes(5);
    private DateTimeOffset _providersRefreshedAt;
    private bool _providersRefreshing;
    private bool _providerSaveInFlight;

    /// <summary>Persists a provider's enabled flag through Core.</summary>
    public async Task SetProviderEnabledAsync(ProviderCardRow provider, bool enabled)
    {
        if (!_providerTogglesInFlight.Add(provider.Id))
        {
            return;
        }

        try
        {
            await _core.SetProviderEnabledAsync(provider.Id, enabled, CancellationToken.None)
                .ConfigureAwait(true);
            provider.Enabled = enabled;
            ProviderStatusText =
                $"已{(enabled ? "启用" : "停用")} {provider.Name}；重新连接 KL Core 后应用到聊天。";
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            provider.Enabled = !enabled;
            ProviderStatusText = $"无法更新 {provider.Name}：{error.Message}";
        }
        finally
        {
            _providerTogglesInFlight.Remove(provider.Id);
        }
    }

    public void OpenProviderForm()
    {
        ProviderFormStatus = string.Empty;
        ProviderStatusText = string.Empty;
        CloseProviderDetail();
        ProviderDraftName = string.Empty;
        ProviderDraftNote = string.Empty;
        ProviderDraftWebsite = string.Empty;
        ProviderDraftChatUrl = string.Empty;
        ProviderDraftAnthropicUrl = string.Empty;
        ProviderDraftResponsesUrl = string.Empty;
        ProviderChatSuffixIndex = 1;
        ProviderAnthropicSuffixIndex = 1;
        ProviderResponsesSuffixIndex = 1;
        ProviderDraftApiKey = string.Empty;
        ProviderChatProtocolEnabled = true;
        ProviderAnthropicProtocolEnabled = false;
        ProviderResponsesProtocolEnabled = false;
        IsProviderFormOpen = true;
    }

    public void CloseProviderForm()
    {
        IsProviderFormOpen = false;
        ProviderDraftApiKey = string.Empty;
        ProviderFormStatus = string.Empty;
    }

    /// <summary>Re-reads Core's provider catalog and config over one Core connection.</summary>
    public async Task RefreshProvidersAsync(bool force = false, bool announce = true)
    {
        if (_providersRefreshing
            || (!force && DateTimeOffset.Now - _providersRefreshedAt < ProviderRefreshCache))
        {
            return;
        }

        _providersRefreshing = true;

        try
        {
            var catalog = await _core.LoadProviderCatalogAsync(CancellationToken.None)
                .ConfigureAwait(true);
            var snapshot = catalog.Snapshot;
            if (!snapshot.Success)
            {
                ProviderStatusText = $"无法读取提供商：{snapshot.Error}";
                return;
            }

            if (!catalog.ProviderListRead)
            {
                ProviderStatusText = "无法刷新提供商：读取 provider.list 失败。";
                return;
            }

            ApplyProviderCards(
                snapshot.ModelOptions,
                snapshot.Models,
                snapshot.Provider,
                snapshot.Provider,
                snapshot.KeyConfigured,
                snapshot.ProviderProtocols);
            MergeConfiguredProviders(catalog.Config);
            EnsureUniqueProviderNames();
            NotifyProviderCardsChanged();
            _providersRefreshedAt = DateTimeOffset.Now;
            if (catalog.ConfigRead)
            {
                SaveProviderCache();
            }
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            ProviderStatusText = $"无法刷新提供商：{error.Message}";
        }
        finally
        {
            _providersRefreshing = false;
        }
    }

    /// <summary>Writes a provider (and optional key) into Core config.</summary>
    public async Task SaveProviderAsync()
    {
        if (_providerSaveInFlight)
        {
            return;
        }

        _providerSaveInFlight = true;
        try
        {
            await SaveProviderCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _providerSaveInFlight = false;
        }
    }

    private async Task SaveProviderCoreAsync()
    {
        var name = ProviderDraftName.Trim();
        if (name.Length == 0)
        {
            ProviderFormStatus = "请填写提供商名称。";
            return;
        }

        var website = ProviderDraftWebsite.Trim();
        if (website.Length > 0
            && (!Uri.TryCreate(website, UriKind.Absolute, out var websiteUri)
                || (websiteUri.Scheme != Uri.UriSchemeHttp
                    && websiteUri.Scheme != Uri.UriSchemeHttps)))
        {
            ProviderFormStatus = "官网链接必须是完整的 http:// 或 https:// 地址。";
            return;
        }

        var baseUrl = ProviderDraftChatUrl.Trim();
        if (baseUrl.Length == 0)
        {
            ProviderFormStatus = "请填写请求地址。";
            return;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            ProviderFormStatus = "请求地址必须是完整的 http:// 或 https:// 地址。";
            return;
        }

        // 添加表单把共享的 chat URL 属性当作基础地址，一次生成三种协议。
        var suffixIndex = ProviderChatSuffixIndex;
        var protocols = new Dictionary<string, string?>
        {
            ["openai-chat"] = AppendEndpointSuffix(
                baseUrl, suffixIndex, "/chat/completions", "/v1/chat/completions"),
            ["anthropic"] = AppendEndpointSuffix(
                baseUrl, suffixIndex, "/messages", "/v1/messages"),
            ["openai-responses"] = AppendEndpointSuffix(
                baseUrl, suffixIndex, "/responses", "/v1/responses")
        };

        var id = ProviderIdFromName(name);
        if (ProviderCards.Any(row => string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            ProviderFormStatus = "这个提供商已存在，请从列表中编辑它。";
            return;
        }

        var parameters = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["enabled"] = true,
            ["protocols"] = protocols,
            ["models"] = Array.Empty<object>()
        };

        var note = ProviderDraftNote.Trim();
        var key = ProviderDraftApiKey.Trim();
        var protocolMap = protocols
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value!,
                StringComparer.OrdinalIgnoreCase);

        // Show the card immediately; the Core write runs after the form closes.
        var inserted = false;
        var card = ProviderCards.FirstOrDefault(existing =>
            string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase));
        if (card is null)
        {
            card = new ProviderCardRow(
                id,
                name,
                note,
                website,
                isDefault: false,
                enabled: true,
                keyConfigured: key.Length > 0,
                models: [],
                protocolMap);
            ProviderCards.Insert(0, card);
            inserted = true;
        }

        EnsureUniqueProviderNames();
        NotifyProviderCardsChanged();
        IsProviderFormOpen = false;
        ProviderStatusText = $"正在写入 {name}…";

        try
        {
            await _core.UpsertProviderWithKeyAsync(
                    parameters,
                    id,
                    key.Length > 0 ? key : null,
                    CancellationToken.None)
                .ConfigureAwait(true);

            var profiles = _state.ProviderProfiles ??= [];
            profiles[id] = new ProviderProfileState
            {
                DisplayName = name,
                Note = note,
                Website = website
            };
            SaveState();

            card.Name = name;
            card.Note = note;
            card.Website = website;
            card.SetKeyConfigured(key.Length > 0);
            card.SetProtocols(protocolMap);
            EnsureUniqueProviderNames();
            SaveProviderCache();

            ProviderDraftApiKey = string.Empty;
            ProviderFormStatus = string.Empty;
            ProviderStatusText = $"已写入 {id}；可在提供商详情中继续添加模型。";
            _ = RefreshProvidersAsync(force: true, announce: false);
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            if (inserted)
            {
                ProviderCards.Remove(card);
                NotifyProviderCardsChanged();
            }

            IsProviderFormOpen = true;
            ProviderStatusText = string.Empty;
            ProviderFormStatus = $"保存提供商失败：{error.Message}";
        }
    }

    private static IReadOnlyDictionary<string, string> SavedProtocols(
        IReadOnlyDictionary<string, string?> protocols) =>
        protocols
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value!,
                StringComparer.OrdinalIgnoreCase);

    private string ProviderChatEndpoint() =>
        AppendEndpointSuffix(
            ProviderDraftChatUrl, ProviderChatSuffixIndex, "/chat/completions", "/v1/chat/completions");

    private string ProviderAnthropicEndpoint() =>
        AppendEndpointSuffix(
            ProviderDraftAnthropicUrl, ProviderAnthropicSuffixIndex, "/messages", "/v1/messages");

    private string ProviderResponsesEndpoint() =>
        AppendEndpointSuffix(
            ProviderDraftResponsesUrl, ProviderResponsesSuffixIndex, "/responses", "/v1/responses");

    private static string AppendEndpointSuffix(
        string url,
        int suffixIndex,
        string shortSuffix,
        string versionedSuffix)
    {
        url = url.Trim().TrimEnd('/');
        if (url.Length == 0)
        {
            return string.Empty;
        }

        var suffix = suffixIndex switch
        {
            1 => shortSuffix,
            2 => versionedSuffix,
            _ => string.Empty
        };
        return suffix.Length > 0 && !url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? url + suffix
            : url;
    }

    private static string ProviderIdFromName(string name)
    {
        var id = NormalizeProviderId(name);
        if (id.Length > 0)
        {
            return id;
        }

        // Non-ASCII names normalize to nothing; derive a stable id so the
        // same name cannot be added twice with different ids.
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(name.Trim()));
        return $"provider-{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    private static string NormalizeProviderId(string value)
    {
        var normalized = string.Concat(value
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.'
                    ? character
                    : '-'))
            .Trim('-', '_', '.');
        return normalized;
    }

    private string ProviderProtocolUrl(int index) => index switch
    {
        1 => ProviderDraftAnthropicUrl.Trim(),
        2 => ProviderDraftResponsesUrl.Trim(),
        _ => ProviderDraftChatUrl.Trim()
    };

    private static string ProviderProtocol(int index) => index switch
    {
        1 => "anthropic",
        2 => "openai-responses",
        _ => "openai-chat"
    };

    private static (int ContextWindow, int MaxOutputTokens) ProviderLimits(string protocol) =>
        protocol switch
        {
            "anthropic" => (200_000, 8_192),
            "openai-responses" => (256_000, 16_384),
            _ => (128_000, 8_192)
        };
}

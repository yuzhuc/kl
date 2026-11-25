using System.Collections.ObjectModel;

namespace Kl.Gui.ViewModels;

public partial class MainWindowViewModel
{
    private static readonly IReadOnlyList<string> FallbackComposerEfforts = ["default", "low", "medium", "high"];
    private string? _composerProvider;
    private string? _composerModel;
    private string? _composerModelLabel;
    private string _composerReasoningEffort = "default";

    /// <summary>Effort levels the composer slider offers for the current model.</summary>
    public ObservableCollection<ReasoningEffortOption> ComposerReasoningEffortOptions { get; } = [];

    public int ComposerReasoningEffortIndex
    {
        get
        {
            for (var i = 0; i < ComposerReasoningEffortOptions.Count; i++)
            {
                if (ComposerReasoningEffortOptions[i].Id == _composerReasoningEffort)
                {
                    return i;
                }
            }
            return 0;
        }
    }

    /// <summary>Rebuilds the composer tiers from the selected model's supported levels.</summary>
    public void RefreshComposerReasoningEfforts()
    {
        var (provider, model) = ComposerModelPair();
        var option = ProviderCards
            .FirstOrDefault(card => string.Equals(card.Id, provider, StringComparison.OrdinalIgnoreCase))
            ?.Models.FirstOrDefault(candidate =>
                string.Equals(candidate.Model, model, StringComparison.OrdinalIgnoreCase));
        _state.ModelReasoningEfforts.TryGetValue(ModelEffortKey(provider, model), out var saved);
        var (ids, current) = ResolveComposerEfforts(
            option?.ReasoningEfforts,
            option?.ReasoningEffort,
            saved);
        if (!ComposerReasoningEffortOptions.Select(candidate => candidate.Id).SequenceEqual(ids))
        {
            ComposerReasoningEffortOptions.Clear();
            foreach (var id in ids)
            {
                ComposerReasoningEffortOptions.Add(new ReasoningEffortOption(id, id));
            }
        }
        ApplyComposerReasoningEffort(current);
    }

    /// <summary>
    /// Picks the composer tiers and the selected tier. A saved choice wins
    /// while the model still supports it; otherwise the model's default, the
    /// nearest supported level below it, then the first tier.
    /// </summary>
    internal static (IReadOnlyList<string> Ids, string Current) ResolveComposerEfforts(
        IReadOnlyList<string>? supported,
        string? modelDefault,
        string? saved)
    {
        var levels = supported is { Count: > 0 } ? supported : FallbackComposerEfforts;
        var ids = levels
            .Select(level => level.Trim().ToLowerInvariant())
            .Where(level => level.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            ids.AddRange(FallbackComposerEfforts);
        }

        var savedId = string.IsNullOrWhiteSpace(saved) ? null : saved.Trim().ToLowerInvariant();
        if (savedId is not null && ids.Contains(savedId, StringComparer.Ordinal))
        {
            return (ids, savedId);
        }

        var defaultId = string.IsNullOrWhiteSpace(modelDefault) || modelDefault == "default"
            ? null
            : modelDefault.Trim().ToLowerInvariant();
        if (defaultId is not null && ids.Contains(defaultId, StringComparer.Ordinal))
        {
            return (ids, defaultId);
        }

        var desired = savedId ?? defaultId;
        var desiredRank = desired is null ? -1 : EffortRank(desired);
        if (desiredRank >= 0)
        {
            var lower = ids
                .Select(id => (Id: id, Rank: EffortRank(id)))
                .Where(candidate => candidate.Rank >= 0 && candidate.Rank < desiredRank)
                .OrderByDescending(candidate => candidate.Rank)
                .Select(candidate => candidate.Id)
                .FirstOrDefault();
            if (lower is not null)
            {
                return (ids, lower);
            }
        }
        return (ids, ids[0]);
    }

    private static int EffortRank(string id) => id switch
    {
        "none" => 0,
        "minimal" => 1,
        "low" => 2,
        "medium" => 3,
        "high" => 4,
        "xhigh" => 5,
        "max" => 6,
        "ultra" => 7,
        _ => -1
    };

    /// <summary>Applies the user's effort to the next turn and remembers it per model.</summary>
    public void SetComposerReasoningEffort(string effort)
    {
        if (!ComposerReasoningEffortOptions.Any(option => option.Id == effort))
        {
            return;
        }

        ApplyComposerReasoningEffort(effort);
        var (provider, model) = ComposerModelPair();
        if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(model))
        {
            _state.ModelReasoningEfforts[ModelEffortKey(provider, model)] = effort;
            SaveState();
        }
    }

    private void ApplyComposerReasoningEffort(string effort)
    {
        _composerReasoningEffort = effort;
        foreach (var option in ComposerReasoningEffortOptions)
        {
            option.IsSelected = option.Id == effort;
        }
        _core.SetTurnReasoningEffort(effort == "default" ? null : effort);
        OnPropertyChanged(nameof(ComposerReasoningEffortIndex));
        RaiseReasoningDisplay();
    }

    private static string ModelEffortKey(string provider, string model) =>
        $"{provider.Trim()}/{model.Trim()}".ToLowerInvariant();

    private (string Provider, string Model) ComposerModelPair()
    {
        if (!string.IsNullOrWhiteSpace(_composerProvider) && !string.IsNullOrWhiteSpace(_composerModel))
        {
            return (_composerProvider, _composerModel);
        }
        return _core.IsSessionOpen
            ? (_core.Provider, _core.Model)
            : (CoreCatalogProvider, CoreCatalogModel);
    }

    public ObservableCollection<ComposerModelRow> ComposerModels { get; } = [];
    public bool HasComposerModels => ComposerModels.Count > 0;

    /// <summary>Rebuilds the composer model list from Core's enabled models.</summary>
    public void RefreshComposerModels()
    {
        ComposerModels.Clear();
        foreach (var choice in SettingsModelChoices)
        {
            var (provider, model) = ParseModelChoice(choice);
            if (provider.Length == 0 || model.Length == 0)
            {
                continue;
            }

            ComposerModels.Add(new ComposerModelRow(FormatModelChoice(provider, model), choice));
        }

        RefreshComposerModelSelection();
        OnPropertyChanged(nameof(HasComposerModels));
    }

    /// <summary>Re-applies the check mark when the picker is opened or the model changes.</summary>
    public void RefreshComposerModelSelection()
    {
        var provider = _composerProvider;
        var model = _composerModel;
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            provider = _core.IsSessionOpen ? _core.Provider : CoreCatalogProvider;
            model = _core.IsSessionOpen ? _core.Model : CoreCatalogModel;
        }

        foreach (var row in ComposerModels)
        {
            var (rowProvider, rowModel) = ParseModelChoice(row.Choice);
            row.IsSelected = string.Equals(rowProvider, provider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(rowModel, model, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Switches the model for this chat (and the first turn of a draft). The
    /// choice is handed to Core with the next turn; the chip updates immediately.
    /// </summary>
    public void SelectComposerModel(ComposerModelRow row)
    {
        var (provider, model) = ParseModelChoice(row.Choice);
        if (provider.Length == 0)
        {
            return;
        }

        _composerProvider = provider;
        _composerModel = model;
        _composerModelLabel = row.Label;
        _core.SetTurnModel(provider, model);
        RefreshComposerModelSelection();
        RefreshComposerReasoningEfforts();
        OnPropertyChanged(nameof(ProviderModelLabel));
    }

    /// <summary>A new draft or another chat starts from Core's own model.</summary>
    private void ResetComposerModel()
    {
        _composerProvider = null;
        _composerModel = null;
        _composerModelLabel = null;
        _core.SetTurnModel(null, null);
        RefreshComposerModelSelection();
        RefreshComposerReasoningEfforts();
        OnPropertyChanged(nameof(ProviderModelLabel));
    }

    private static string FormatModelChoice(string provider, string model) =>
        string.IsNullOrWhiteSpace(provider)
            ? PrettifyWords(model)
            : model.StartsWith(provider, StringComparison.OrdinalIgnoreCase)
                ? PrettifyWords(model)
                : $"{PrettifyWords(provider)} {PrettifyWords(model)}";
}

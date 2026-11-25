using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;

namespace Kl.Gui.Services;

/// <summary>One catalog entry: the Simplified Chinese source and its English translation.</summary>
public sealed record LocalizationEntry(
    [property: JsonPropertyName("zh")] string Zh,
    [property: JsonPropertyName("en")] string En);

/// <summary>
/// Loads the embedded zh/en catalog and publishes the active language as
/// application resources so XAML can use {DynamicResource S.xxxxxxxx}.
/// </summary>
public static class Localization
{
    public const string Chinese = "zh-Hans";
    public const string English = "en";

    private static readonly Dictionary<string, LocalizationEntry> ById = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LocalizationEntry> ByChinese = new(StringComparer.Ordinal);
    private static ResourceDictionary? _dictionary;

    public static event Action? LanguageChanged;

    public static string CurrentLanguage { get; private set; } = Chinese;

    public static bool IsEnglish => CurrentLanguage == English;

    static Localization()
    {
        LoadCatalog();
    }

    /// <summary>Translates a catalog id or a Chinese source string.</summary>
    public static string T(string zhOrId)
    {
        var entry = Find(zhOrId);
        return entry is null ? zhOrId : IsEnglish ? entry.En : entry.Zh;
    }

    /// <summary>Translates a template and formats it with the current culture.</summary>
    public static string F(string template, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(template), args);

    /// <summary>Resolves "" (follow system), zh-Hans or en.</summary>
    public static string Resolve(string? setting) => setting switch
    {
        Chinese => Chinese,
        English => English,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? Chinese : English
    };

    /// <summary>Activates a language and republishes every catalog string as a resource.</summary>
    public static void Apply(string? setting)
    {
        var language = Resolve(setting);
        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CurrentLanguage = language;
        PublishResources();
        LanguageChanged?.Invoke();
    }

    /// <summary>All catalog ids, used by the completeness check.</summary>
    public static IReadOnlyCollection<string> Keys => ById.Keys;
    public static IReadOnlyDictionary<string, LocalizationEntry> Entries => ById;

    public static IReadOnlyCollection<string> ChineseSources => ByChinese.Keys;

    private static LocalizationEntry? Find(string key)
    {
        if (ById.TryGetValue(key, out var byId))
        {
            return byId;
        }
        return ByChinese.GetValueOrDefault(key);
    }

    private static void PublishResources()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        // Swap one dictionary instead of raising a change for every key.
        var dictionary = new ResourceDictionary();
        foreach (var (id, entry) in ById)
        {
            dictionary[id] = IsEnglish ? entry.En : entry.Zh;
        }

        if (_dictionary is not null)
        {
            application.Resources.MergedDictionaries.Remove(_dictionary);
        }
        _dictionary = dictionary;
        application.Resources.MergedDictionaries.Add(dictionary);
    }

    private static void LoadCatalog()
    {
        var assembly = typeof(Localization).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(resource => resource.EndsWith("i18n.catalog.json", StringComparison.Ordinal));
        if (name is null)
        {
            return;
        }

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return;
        }

        var entries = JsonSerializer.Deserialize<Dictionary<string, LocalizationEntry>>(stream);
        if (entries is null)
        {
            return;
        }
        foreach (var (id, entry) in entries)
        {
            ById[id] = entry;
            if (!string.IsNullOrEmpty(entry.Zh))
            {
                ByChinese[entry.Zh] = entry;
            }
        }
    }
}

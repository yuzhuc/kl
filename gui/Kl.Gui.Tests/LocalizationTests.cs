using System.Text.RegularExpressions;
using Kl.Gui.Services;

namespace Kl.Gui.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void CatalogContainsCompleteBilingualEntries()
    {
        Assert.NotEmpty(Localization.Entries);
        foreach (var (id, entry) in Localization.Entries)
        {
            Assert.StartsWith("S.", id);
            Assert.False(string.IsNullOrWhiteSpace(entry.Zh), id);
            Assert.False(string.IsNullOrWhiteSpace(entry.En), id);
        }
    }

    [Fact]
    public void TranslationsKeepPlaceholderIndexes()
    {
        var placeholders = new Regex(@"\{(\d+)(?:[,:][^}]*)?\}");
        foreach (var (id, entry) in Localization.Entries)
        {
            var zh = placeholders.Matches(entry.Zh).Select(match => match.Groups[1].Value).Order();
            var en = placeholders.Matches(entry.En).Select(match => match.Groups[1].Value).Order();
            Assert.Equal(zh, en);
        }
    }

    [Fact]
    public void UnknownTextFallsBackToTheSource()
    {
        Assert.Equal("没有这个条目", Localization.T("没有这个条目"));
    }
}

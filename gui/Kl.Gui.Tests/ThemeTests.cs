using Kl.Gui.Controls;
using Xunit;

namespace Kl.Gui.Tests;

public sealed class ThemeTests
{
    [Fact]
    public void ThemeSettingsRoundTripAndRouteByActiveVariant()
    {
        using var temp = new TempDirectory();
        var store = temp.CreateStore();
        TestData.Seed(store, temp.Path);
        var vm = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());

        Assert.Equal("dark", vm.ThemeMode);
        Assert.True(vm.IsThemeDark);

        vm.SetThemeMode("light");
        vm.SeparateThemeSettings = true;
        vm.SetEffectiveTheme(true);
        vm.AccentColorIndex = 2;
        vm.Contrast = 70;
        vm.SetEffectiveTheme(false);
        vm.AccentColorIndex = 1;
        vm.Contrast = 30;
        vm.SetDiffMarkerMode("markers");
        vm.SaveAppearanceSettings();

        var saved = store.Load();
        Assert.Equal("light", saved.ThemeMode);
        Assert.True(saved.SeparateThemeSettings);
        Assert.Equal("#179E8A", saved.LightAccentColor);
        Assert.Equal("#3566F0", saved.DarkAccentColor);
        Assert.Equal(70, saved.LightContrast);
        Assert.Equal(30, saved.DarkContrast);
        Assert.Equal("markers", saved.DiffMarkerMode);

        var reloaded = TestData.CreateViewModel(temp, store, new FakeCoreSessionService());
        Assert.True(reloaded.IsThemeLight);
        Assert.True(reloaded.IsDiffMarkers);
        Assert.False(reloaded.IsDiffColorMode);
        reloaded.SetEffectiveTheme(true);
        Assert.Equal(2, reloaded.AccentColorIndex);
        Assert.Equal(70, reloaded.Contrast);
        reloaded.SetEffectiveTheme(false);
        Assert.Equal(1, reloaded.AccentColorIndex);
        Assert.Equal(30, reloaded.Contrast);
    }

    [Fact]
    public void DiffFormatterStripsSignsInColorModeAndKeepsThemForMarkers()
    {
        const string diff = "--- a/src/a.rs\n+++ b/src/a.rs\n@@ -1 +1 @@\n-old\n+new\n context";

        var colored = DiffTextFormatter.Format(diff, useMarkers: false);
        Assert.Equal(DiffLineKind.Meta, colored[0].Kind);
        Assert.Equal(DiffLineKind.Meta, colored[1].Kind);
        Assert.Equal(DiffLineKind.Hunk, colored[2].Kind);
        Assert.Equal("old", colored[3].Text);
        Assert.Equal(DiffLineKind.Remove, colored[3].Kind);
        Assert.Equal("new", colored[4].Text);
        Assert.Equal(DiffLineKind.Add, colored[4].Kind);

        var markers = DiffTextFormatter.Format(diff, useMarkers: true);
        Assert.Equal("-old", markers[3].Text);
        Assert.Equal(DiffLineKind.Normal, markers[3].Kind);
        Assert.Equal("+new", markers[4].Text);
        Assert.Equal(DiffLineKind.Normal, markers[4].Kind);
    }
}

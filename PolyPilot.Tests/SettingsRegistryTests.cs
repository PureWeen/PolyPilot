using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

public class SettingsRegistryTests
{
    private static SettingsContext CreateContext(ConnectionSettings? settings = null)
    {
        var s = settings ?? new ConnectionSettings();
        return new SettingsContext
        {
            Settings = s,
            FontSize = 20,
            ServerAlive = false,
            IsDesktop = true,
            InitialMode = s.Mode
        };
    }

    [Fact]
    public void All_ReturnsNonEmptyList()
    {
        Assert.NotEmpty(SettingsRegistry.All);
    }

    [Fact]
    public void Categories_ContainsExpectedCategories()
    {
        var cats = SettingsRegistry.Categories;
        Assert.Contains("Connection", cats);
        Assert.Contains("UI", cats);
    }

    [Fact]
    public void ForCategory_ReturnsOnlyMatchingCategory()
    {
        var ctx = CreateContext();
        var uiSettings = SettingsRegistry.ForCategory("UI", ctx).ToList();
        Assert.NotEmpty(uiSettings);
        Assert.All(uiSettings, s => Assert.Equal("UI", s.Category));
    }

    [Fact]
    public void ForCategory_RespectsVisibilityPredicate()
    {
        var settings = new ConnectionSettings { Mode = ConnectionMode.Remote };
        var ctx = CreateContext(settings);
        var cliSettings = SettingsRegistry.ForCategory("Copilot CLI", ctx).ToList();
        // CLI source is hidden in Remote mode
        Assert.DoesNotContain(cliSettings, s => s.Id == "cli.source");
    }

    [Fact]
    public void Search_MatchesByLabel()
    {
        var ctx = CreateContext();
        var results = SettingsRegistry.Search("Font Size", ctx).ToList();
        Assert.Contains(results, s => s.Id == "ui.fontSize");
    }

    [Fact]
    public void Search_MatchesByKeywords()
    {
        var ctx = CreateContext();
        var results = SettingsRegistry.Search("notifications", ctx).ToList();
        Assert.Contains(results, s => s.Id == "ui.notifications");
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsAllVisible()
    {
        var ctx = CreateContext();
        var all = SettingsRegistry.Search("", ctx).ToList();
        Assert.True(all.Count >= 5);
    }

    [Fact]
    public void ColorScheme_GetValue_ReturnsDarkForPolyPilotDark()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.PolyPilotDark };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.colorScheme");
        Assert.Equal("Dark", desc.GetValue!(ctx));
    }

    [Fact]
    public void ColorScheme_SetValue_SetsCorrectTheme()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.PolyPilotDark };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.colorScheme");
        desc.SetValue!(ctx, "Light");
        Assert.Equal(UiTheme.PolyPilotLight, settings.Theme);
    }

    [Fact]
    public void ColorScheme_SetValue_PreservesSolarized()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.SolarizedDark };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.colorScheme");
        desc.SetValue!(ctx, "Light");
        Assert.Equal(UiTheme.SolarizedLight, settings.Theme);
    }

    [Fact]
    public void ColorScheme_SystemPreservesSolarized()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.SolarizedDark };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.colorScheme");
        desc.SetValue!(ctx, "System");
        Assert.Equal(UiTheme.SystemSolarized, settings.Theme);
    }

    [Fact]
    public void ThemeStyle_GetValue_ReturnsSolarized()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.SolarizedDark };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.themeStyle");
        Assert.Equal("Solarized", desc.GetValue!(ctx));
    }

    [Fact]
    public void ThemeStyle_SetValue_SwitchesToSolarized()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.PolyPilotDark };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.themeStyle");
        desc.SetValue!(ctx, "Solarized");
        Assert.Equal(UiTheme.SolarizedDark, settings.Theme);
    }

    [Fact]
    public void ThemeStyle_VisibleEvenWhenSystemTheme()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.System };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.themeStyle");
        // Style picker is always visible so user can choose PolyPilot vs Solarized
        Assert.Null(desc.IsVisible);
    }

    [Fact]
    public void ThemeStyle_SystemPlusSolarized()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.System };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.themeStyle");
        desc.SetValue!(ctx, "Solarized");
        Assert.Equal(UiTheme.SystemSolarized, settings.Theme);
    }

    [Fact]
    public void ThemeStyle_SystemSolarizedBackToPolyPilot()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.SystemSolarized };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.themeStyle");
        Assert.Equal("Solarized", desc.GetValue!(ctx));
        desc.SetValue!(ctx, "PolyPilot");
        Assert.Equal(UiTheme.System, settings.Theme);
    }

    [Fact]
    public void ColorScheme_SystemSolarizedReturnsSystem()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.SystemSolarized };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.colorScheme");
        Assert.Equal("System", desc.GetValue!(ctx));
    }

    [Fact]
    public void FontSize_GetValue_ReturnsContextFontSize()
    {
        var ctx = CreateContext();
        ctx.FontSize = 18;
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.fontSize");
        Assert.Equal(18, desc.GetValue!(ctx));
    }

    [Fact]
    public void FontSize_SetValue_ClampsBetweenMinMax()
    {
        var ctx = CreateContext();
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.fontSize");
        desc.SetValue!(ctx, 8);
        Assert.Equal(12, ctx.FontSize);
        desc.SetValue!(ctx, 30);
        Assert.Equal(24, ctx.FontSize);
    }

    [Fact]
    public void Notifications_ToggleValue()
    {
        var settings = new ConnectionSettings { EnableSessionNotifications = false };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.notifications");
        Assert.False((bool)desc.GetValue!(ctx)!);
        desc.SetValue!(ctx, true);
        Assert.True(settings.EnableSessionNotifications);
    }

    [Fact]
    public void ChatLayout_SetValue_ParsesEnum()
    {
        var settings = new ConnectionSettings();
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.chatLayout");
        desc.SetValue!(ctx, "Reversed");
        Assert.Equal(ChatLayout.Reversed, settings.ChatLayout);
    }

    [Fact]
    public void SetValue_DoesNotCallSave()
    {
        // Verify that SetValue lambdas don't call Save() — the page handles saving.
        // This is verified by the fact that we use ConnectionSettings directly
        // (no mock) and the test doesn't throw despite not having a valid file path.
        var settings = new ConnectionSettings { Theme = UiTheme.PolyPilotDark };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.colorScheme");
        desc.SetValue!(ctx, "Light");
        // If Save() were called, it would attempt to write to a file.
        // The test passes = no Save() called.
        Assert.Equal(UiTheme.PolyPilotLight, settings.Theme);
    }

    // ── Amber theme cross-product tests ─────────────────────────────

    [Fact]
    public void ColorScheme_SetValue_PreservesAmber()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.AmberDark };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.colorScheme");
        desc.SetValue!(ctx, "Light");
        Assert.Equal(UiTheme.AmberLight, settings.Theme);
    }

    [Fact]
    public void ColorScheme_SystemPreservesAmber()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.AmberDark };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.colorScheme");
        desc.SetValue!(ctx, "System");
        Assert.Equal(UiTheme.SystemAmber, settings.Theme);
    }

    [Fact]
    public void ThemeStyle_GetValue_ReturnsAmber()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.AmberDark };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.themeStyle");
        Assert.Equal("Amber", desc.GetValue!(ctx));
    }

    [Fact]
    public void ThemeStyle_SetValue_SwitchesToAmber()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.PolyPilotDark };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.themeStyle");
        desc.SetValue!(ctx, "Amber");
        Assert.Equal(UiTheme.AmberDark, settings.Theme);
    }

    [Fact]
    public void ThemeStyle_SystemPlusAmber()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.System };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.themeStyle");
        desc.SetValue!(ctx, "Amber");
        Assert.Equal(UiTheme.SystemAmber, settings.Theme);
    }

    [Fact]
    public void ThemeStyle_SystemAmberBackToPolyPilot()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.SystemAmber };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.themeStyle");
        Assert.Equal("Amber", desc.GetValue!(ctx));
        desc.SetValue!(ctx, "PolyPilot");
        Assert.Equal(UiTheme.System, settings.Theme);
    }

    [Fact]
    public void ColorScheme_SystemAmberReturnsSystem()
    {
        var settings = new ConnectionSettings { Theme = UiTheme.SystemAmber };
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.colorScheme");
        Assert.Equal("System", desc.GetValue!(ctx));
    }

    [Fact]
    public void EachDescriptor_HasUniqueId()
    {
        var ids = SettingsRegistry.All.Select(s => s.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void EachDescriptor_HasLabel()
    {
        Assert.All(SettingsRegistry.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Label)));
    }

    [Fact]
    public void EachDescriptor_HasCategory()
    {
        Assert.All(SettingsRegistry.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Category)));
    }

    [Fact]
    public void Editor_GetValue_ReturnsStableByDefault()
    {
        var ctx = CreateContext();
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.editor");
        Assert.Equal("Stable", desc.GetValue!(ctx));
    }

    [Fact]
    public void Editor_SetValue_ParsesInsiders()
    {
        var settings = new ConnectionSettings();
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.editor");
        desc.SetValue!(ctx, "Insiders");
        Assert.Equal(VsCodeVariant.Insiders, settings.Editor);
    }

    [Fact]
    public void Editor_SetValue_GarbageValue_KeepsDefault()
    {
        var settings = new ConnectionSettings();
        var ctx = CreateContext(settings);
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.editor");
        desc.SetValue!(ctx, "garbage");
        Assert.Equal(VsCodeVariant.Stable, settings.Editor);
    }

    [Fact]
    public void Editor_VisibleOnDesktopOnly()
    {
        var ctx = CreateContext();
        ctx.IsDesktop = true;
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.editor");
        Assert.True(desc.IsVisible!(ctx));

        ctx.IsDesktop = false;
        Assert.False(desc.IsVisible!(ctx));
    }
}

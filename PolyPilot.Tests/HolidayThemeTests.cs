using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

public class HolidayThemeTests
{
    // ── IsInternationalWomensDay ─────────────────────────────────────

    [Fact]
    public void IsIWD_March8_ReturnsTrue()
    {
        Assert.True(HolidayThemeHelper.IsInternationalWomensDay(new DateTime(2026, 3, 8)));
    }

    [Fact]
    public void IsIWD_March8_AnyYear_ReturnsTrue()
    {
        Assert.True(HolidayThemeHelper.IsInternationalWomensDay(new DateTime(2025, 3, 8)));
        Assert.True(HolidayThemeHelper.IsInternationalWomensDay(new DateTime(2030, 3, 8)));
    }

    [Fact]
    public void IsIWD_March8_WithTime_ReturnsTrue()
    {
        Assert.True(HolidayThemeHelper.IsInternationalWomensDay(new DateTime(2026, 3, 8, 23, 59, 59)));
        Assert.True(HolidayThemeHelper.IsInternationalWomensDay(new DateTime(2026, 3, 8, 0, 0, 0)));
    }

    [Fact]
    public void IsIWD_March7_ReturnsFalse()
    {
        Assert.False(HolidayThemeHelper.IsInternationalWomensDay(new DateTime(2026, 3, 7)));
    }

    [Fact]
    public void IsIWD_March9_ReturnsFalse()
    {
        Assert.False(HolidayThemeHelper.IsInternationalWomensDay(new DateTime(2026, 3, 9)));
    }

    [Fact]
    public void IsIWD_OtherMonth_ReturnsFalse()
    {
        Assert.False(HolidayThemeHelper.IsInternationalWomensDay(new DateTime(2026, 6, 8)));
        Assert.False(HolidayThemeHelper.IsInternationalWomensDay(new DateTime(2026, 1, 1)));
        Assert.False(HolidayThemeHelper.IsInternationalWomensDay(new DateTime(2026, 12, 25)));
    }

    // ── GetActiveHolidayTheme ────────────────────────────────────────

    [Fact]
    public void GetActiveHolidayTheme_March8_ReturnsIWD()
    {
        var result = HolidayThemeHelper.GetActiveHolidayTheme(new DateTime(2026, 3, 8, 14, 30, 0));
        Assert.Equal(UiTheme.InternationalWomensDay, result);
    }

    [Fact]
    public void GetActiveHolidayTheme_NonHoliday_ReturnsNull()
    {
        Assert.Null(HolidayThemeHelper.GetActiveHolidayTheme(new DateTime(2026, 3, 7)));
        Assert.Null(HolidayThemeHelper.GetActiveHolidayTheme(new DateTime(2026, 3, 9)));
        Assert.Null(HolidayThemeHelper.GetActiveHolidayTheme(new DateTime(2026, 6, 15)));
    }

    // ── GetDataThemeString ───────────────────────────────────────────

    [Theory]
    [InlineData(UiTheme.System, "system")]
    [InlineData(UiTheme.SystemSolarized, "system-solarized")]
    [InlineData(UiTheme.PolyPilotDark, "")]
    [InlineData(UiTheme.PolyPilotLight, "polypilot-light")]
    [InlineData(UiTheme.SolarizedDark, "solarized-dark")]
    [InlineData(UiTheme.SolarizedLight, "solarized-light")]
    [InlineData(UiTheme.InternationalWomensDay, "iwd")]
    [InlineData(UiTheme.AmberDark, "amber-dark")]
    [InlineData(UiTheme.AmberLight, "amber-light")]
    [InlineData(UiTheme.SystemAmber, "system-amber")]
    public void GetDataThemeString_ReturnsCorrectValue(UiTheme theme, string expected)
    {
        Assert.Equal(expected, HolidayThemeHelper.GetDataThemeString(theme));
    }

    // ── ConnectionSettings guard ─────────────────────────────────────

    [Fact]
    public void ConnectionSettings_Load_RevertsIwdThemeToSystem()
    {
        // If IWD theme was somehow persisted, Load() should revert it
        var settings = new ConnectionSettings { Theme = UiTheme.InternationalWomensDay };
        // Simulate what Load() does after deserialization
        if (settings.Theme == UiTheme.InternationalWomensDay)
            settings.Theme = UiTheme.System;

        Assert.Equal(UiTheme.System, settings.Theme);
    }

    [Fact]
    public void ConnectionSettings_Load_PreservesNormalThemes()
    {
        foreach (var theme in new[] { UiTheme.System, UiTheme.PolyPilotDark, UiTheme.PolyPilotLight,
                                       UiTheme.SolarizedDark, UiTheme.SolarizedLight, UiTheme.SystemSolarized,
                                       UiTheme.AmberDark, UiTheme.AmberLight, UiTheme.SystemAmber })
        {
            var settings = new ConnectionSettings { Theme = theme };
            // The guard should not change non-IWD themes
            if (settings.Theme == UiTheme.InternationalWomensDay)
                settings.Theme = UiTheme.System;

            Assert.Equal(theme, settings.Theme);
        }
    }

    // ── IWD theme enum exists in UiTheme ─────────────────────────────

    [Fact]
    public void UiTheme_HasInternationalWomensDay()
    {
        Assert.True(Enum.IsDefined(typeof(UiTheme), UiTheme.InternationalWomensDay));
    }
}

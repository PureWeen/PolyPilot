namespace PolyPilot.Services;

using PolyPilot.Models;

/// <summary>
/// Detects holiday dates and provides automatic theme overrides.
/// All methods accept an optional DateTime for testability.
/// </summary>
public static class HolidayThemeHelper
{
    /// <summary>Returns true if the given date (local time) is March 8.</summary>
    public static bool IsInternationalWomensDay(DateTime? now = null)
    {
        var date = (now ?? DateTime.Now).Date;
        return date.Month == 3 && date.Day == 8;
    }

    /// <summary>
    /// Returns the holiday theme that should be active for the given date,
    /// or null if no holiday theme applies.
    /// </summary>
    public static UiTheme? GetActiveHolidayTheme(DateTime? now = null)
    {
        if (IsInternationalWomensDay(now))
            return UiTheme.InternationalWomensDay;
        return null;
    }

    /// <summary>Maps a UiTheme to the CSS data-theme attribute value.</summary>
    public static string GetDataThemeString(UiTheme theme) => theme switch
    {
        UiTheme.System => "system",
        UiTheme.SystemSolarized => "system-solarized",
        UiTheme.SystemAmber => "system-amber",
        UiTheme.PolyPilotDark => "",
        UiTheme.PolyPilotLight => "polypilot-light",
        UiTheme.SolarizedDark => "solarized-dark",
        UiTheme.SolarizedLight => "solarized-light",
        UiTheme.AmberDark => "amber-dark",
        UiTheme.AmberLight => "amber-light",
        UiTheme.InternationalWomensDay => "iwd",
        _ => ""
    };
}

using Orion.Infrastructure.Storage;

namespace Orion.Desktop.Theming;

public static class ThemeClock
{
    public static string Resolve(string? mode, AppearanceSettings settings, DateTimeOffset now) => mode switch
    {
        "schedule" => IsNight(now.Hour * 60 + now.Minute, settings.DarkStart, settings.DarkEnd) ? "dark" : "light",
        "solar" => SunIsUp(now, settings.Latitude, settings.Longitude) ? "light" : "dark",
        "light" => "light", "system" => "system", _ => "dark"
    };
    // Equal endpoints mean dark all day; both overnight and daytime intervals work.
    public static bool IsNight(int minute, int start, int end) => start >= end ? minute >= start || minute < end : minute >= start && minute < end;

    /// <summary>Approximate solar elevation (NOAA fractional-year equations), including polar day/night.
    /// Coordinates remain local; no geolocation request or external service.</summary>
    public static bool SunIsUp(DateTimeOffset now, double latitude, double longitude)
    {
        var utc = now.UtcDateTime;
        var hour = utc.TimeOfDay.TotalHours;
        var gamma = 2 * Math.PI / (DateTime.IsLeapYear(utc.Year) ? 366 : 365) * (utc.DayOfYear - 1 + (hour - 12) / 24);
        var eq = 229.18 * (.000075 + .001868 * Math.Cos(gamma) - .032077 * Math.Sin(gamma)
            - .014615 * Math.Cos(2 * gamma) - .040849 * Math.Sin(2 * gamma));
        var decl = .006918 - .399912 * Math.Cos(gamma) + .070257 * Math.Sin(gamma)
            - .006758 * Math.Cos(2 * gamma) + .000907 * Math.Sin(2 * gamma)
            - .002697 * Math.Cos(3 * gamma) + .00148 * Math.Sin(3 * gamma);
        var angle = (hour * 60 + eq + 4 * longitude) / 4 - 180;
        var lat = Math.Clamp(latitude, -90, 90) * Math.PI / 180;
        var elevation = Math.Asin(Math.Sin(lat) * Math.Sin(decl) + Math.Cos(lat) * Math.Cos(decl) * Math.Cos(angle * Math.PI / 180));
        return elevation * 180 / Math.PI > -.833;
    }
}

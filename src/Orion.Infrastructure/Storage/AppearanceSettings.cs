namespace Orion.Infrastructure.Storage;

/// <summary>Persisted values only. Normalization is also applied to hand-edited/older settings.</summary>
public sealed record AppearanceSettings
{
    public AppearanceProfile Light { get; init; } = new();
    public AppearanceProfile Dark { get; init; } = new();
    public bool TopNavigation { get; init; }
    public bool ReduceMotion { get; init; }
    public bool KeepWindowOpaque { get; init; }
    public int DarkStart { get; init; } = 20 * 60;
    public int DarkEnd { get; init; } = 7 * 60;
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string WallpaperDirectory { get; init; } = "";
    public string WallpaperFile { get; init; } = "";
    public bool RotateWallpapers { get; init; }
    public int WallpaperIntervalMinutes { get; init; } = 5;
    public string WallpaperFit { get; init; } = "smart";
    public bool RandomWallpaper { get; init; } = true;
    public double WallpaperBlur { get; init; }
    public double WallpaperBrightness { get; init; } = 100;
    public double WallpaperOpacity { get; init; } = 100;
    public AppearanceSettings Normalize() => this with
    {
        Light = (Light ?? new()).Normalize(), Dark = (Dark ?? new()).Normalize(),
        DarkStart = Math.Clamp(DarkStart, 0, 1439), DarkEnd = Math.Clamp(DarkEnd, 0, 1439),
        Latitude = Limit(Latitude, -90, 90, 0), Longitude = Limit(Longitude, -180, 180, 0),
        WallpaperDirectory = WallpaperDirectory is { Length: <= 4096 } && (WallpaperDirectory.Length == 0 || Path.IsPathFullyQualified(WallpaperDirectory)) ? WallpaperDirectory : "",
        WallpaperFile = WallpaperFile is { Length: <= 4096 } && (WallpaperFile.Length == 0 || Path.IsPathFullyQualified(WallpaperFile)) ? WallpaperFile : "",
        WallpaperIntervalMinutes = Math.Clamp(WallpaperIntervalMinutes, 1, 1440),
        WallpaperFit = WallpaperFit is "smart" or "center" or "fit" or "stretch" or "tile" or "top_left" or "top_right" ? WallpaperFit : "smart",
        WallpaperBlur = Limit(WallpaperBlur, 0, 50, 0),
        WallpaperBrightness = Limit(WallpaperBrightness, 20, 100, 100),
        WallpaperOpacity = Limit(WallpaperOpacity, 0, 100, 100)
    };
    internal static double Limit(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    internal static string Hex(string? value, string fallback) => value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0 ? value.ToUpperInvariant() : fallback;
}

public sealed record AppearanceProfile
{
    // "inherit" preserves the original shared palette in existing configurations.
    public string Accent { get; init; } = "inherit";
    public string CustomAccent { get; init; } = "#10B981";
    public string BaseMode { get; init; } = "original";
    public string BaseColor { get; init; } = "#68768A";
    public double BaseOpacity { get; init; } = 20;
    public double SurfaceOpacity { get; init; } = 100;
    public double SurfaceBlur { get; init; }
    public double OverlayOpacity { get; init; }
    public bool ProtectReadability { get; init; } = true;
    public AppearanceProfile Normalize() => this with
    {
        Accent = Accent is { Length: <= 64 } ? Accent : "inherit",
        CustomAccent = AppearanceSettings.Hex(CustomAccent, "#10B981"),
        BaseMode = BaseMode is "none" or "theme" or "color" ? BaseMode : "original",
        BaseColor = AppearanceSettings.Hex(BaseColor, "#68768A"),
        BaseOpacity = AppearanceSettings.Limit(BaseOpacity, 0, 100, 20),
        SurfaceOpacity = AppearanceSettings.Limit(SurfaceOpacity, 0, 100, 100),
        SurfaceBlur = AppearanceSettings.Limit(SurfaceBlur, 0, 32, 0),
        OverlayOpacity = AppearanceSettings.Limit(OverlayOpacity, 0, 80, 0)
    };
}

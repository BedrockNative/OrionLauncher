using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orion.Desktop.I18n;
using Orion.Desktop.Theming;
using Orion.Infrastructure.Storage;

namespace Orion.Desktop.ViewModels;

public partial class AppearanceProfileViewModel : ObservableObject
{
    private string lastCustom = "#10B981";
    private string lastBase = "#68768A";
    private readonly bool light;
    public Localizer Text { get; }
    public IReadOnlyList<ThemeOptionViewModel> Accents { get; }
    public IReadOnlyList<ThemeOptionViewModel> Bases { get; }
    [ObservableProperty] private int accentIndex;
    [ObservableProperty] private string customAccent = "#10B981";
    [ObservableProperty] private int baseIndex;
    [ObservableProperty] private string baseColor = "#68768A";
    [ObservableProperty] private double baseOpacity = 20;
    [ObservableProperty] private double surfaceOpacity = 100;
    [ObservableProperty] private double surfaceBlur;
    [ObservableProperty] private double overlayOpacity;
    [ObservableProperty] private bool protectReadability = true;
    public bool IsCustom => Accents[Math.Clamp(AccentIndex, 0, Accents.Count - 1)].Id == "custom";
    public bool IsCustomBase => BaseIndex == 3;
    public bool IsTintedBase => BaseIndex is 2 or 3;
    public bool ColorsValid => ValidHex(CustomAccent) && ValidHex(BaseColor);
    private static bool ValidHex(string value) => value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
    partial void OnAccentIndexChanged(int value) => OnPropertyChanged(nameof(IsCustom));
    partial void OnBaseIndexChanged(int value) { OnPropertyChanged(nameof(IsCustomBase)); OnPropertyChanged(nameof(IsTintedBase)); }
    partial void OnCustomAccentChanged(string value) => OnPropertyChanged(nameof(ColorsValid));
    partial void OnBaseColorChanged(string value) => OnPropertyChanged(nameof(ColorsValid));
    public AppearanceProfileViewModel(AppearanceProfile profile, Localizer text, bool light, AppearanceProfileViewModel? sharedOptions = null)
    {
        Text = text; this.light = light;
        Accents = sharedOptions?.Accents ?? [new("inherit", "InheritAccent"), new("custom", "CustomColor"),
            .. ThemeManager.Palettes.Concat(AccentCatalog.All).Select(p => new ThemeOptionViewModel(p.Id, p.LabelKey, p.Swatch))];
        Bases = sharedOptions?.Bases ?? [new("original", "BaseOriginal"), new("none", "BaseNeutral"), new("theme", "BaseAccent"), new("color", "CustomColor")];
        profile = profile.Normalize();
        accentIndex = Math.Max(0, Accents.ToList().FindIndex(a => a.Id == profile.Accent));
        customAccent = profile.CustomAccent;
        baseIndex = Math.Max(0, Bases.ToList().FindIndex(a => a.Id == profile.BaseMode));
        baseColor = profile.BaseColor; baseOpacity = profile.BaseOpacity;
        surfaceOpacity = profile.SurfaceOpacity; surfaceBlur = profile.SurfaceBlur;
        overlayOpacity = profile.OverlayOpacity; protectReadability = profile.ProtectReadability;
        RefreshLabels();
    }
    public void RefreshLabels() { foreach (var option in Accents.Concat(Bases)) option.Label = Text[option.LabelKey]; }
    public void Restore(AppearanceProfile profile)
    {
        profile = profile.Normalize();
        AccentIndex = Math.Max(0, Accents.ToList().FindIndex(a => a.Id == profile.Accent));
        CustomAccent = profile.CustomAccent;
        BaseIndex = Math.Max(0, Bases.ToList().FindIndex(a => a.Id == profile.BaseMode));
        BaseColor = profile.BaseColor; BaseOpacity = profile.BaseOpacity;
        SurfaceOpacity = profile.SurfaceOpacity; SurfaceBlur = profile.SurfaceBlur;
        OverlayOpacity = profile.OverlayOpacity; ProtectReadability = profile.ProtectReadability;
    }
    public AppearanceProfile Snapshot()
    {
        if (ValidHex(CustomAccent)) lastCustom = CustomAccent;
        if (ValidHex(BaseColor)) lastBase = BaseColor;
        return new AppearanceProfile
        {
            Accent = Accents[Math.Clamp(AccentIndex, 0, Accents.Count - 1)].Id, CustomAccent = lastCustom,
            BaseMode = Bases[Math.Clamp(BaseIndex, 0, Bases.Count - 1)].Id, BaseColor = lastBase, BaseOpacity = BaseOpacity,
            SurfaceOpacity = SurfaceOpacity, SurfaceBlur = SurfaceBlur, OverlayOpacity = OverlayOpacity, ProtectReadability = ProtectReadability
        }.Normalize();
    }
    [RelayCommand] private void Material(string preset)
    {
        SurfaceOpacity = preset switch { "clear" => 35, "balanced" => 60, _ => 100 };
        SurfaceBlur = preset switch { "clear" => 4, "balanced" => 12, _ => 0 };
        OverlayOpacity = preset == "solid" ? 0 : light ? 8 : 18;
        ProtectReadability = preset == "solid";
    }
}

public partial class AdvancedAppearanceViewModel : ObservableObject
{
    public Localizer Text { get; }
    public AppearanceProfileViewModel Light { get; }
    public AppearanceProfileViewModel Dark { get; }
    public IReadOnlyList<ThemeOptionViewModel> Fits { get; }
    public event Action? Changed;
    public event Action? NextWallpaperRequested;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Profile))] private bool editLight;
    public AppearanceProfileViewModel Profile => EditLight ? Light : Dark;
    [ObservableProperty] private bool topNavigation;
    [ObservableProperty] private bool reduceMotion;
    [ObservableProperty] private bool keepWindowOpaque;
    [ObservableProperty] private string desktopOpacityStatus = "";
    public string DesktopEnvironmentName => DesktopTransparency.Detect().Name;
    public string DesktopOpacityGuide => DesktopTransparency.Guide(Text);
    [ObservableProperty] private TimeSpan darkStart;
    [ObservableProperty] private TimeSpan darkEnd;
    [ObservableProperty] private double latitude;
    [ObservableProperty] private double longitude;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasWallpaperFolder))] private string wallpaperDirectory = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasWallpaperFolder))] private string wallpaperFile = "";
    [ObservableProperty] private bool rotateWallpapers;
    [ObservableProperty] private int wallpaperIntervalMinutes = 5;
    public bool HasWallpaperFolder => WallpaperFile.Length == 0 && WallpaperDirectory.Length > 0;
    [ObservableProperty] private int fitIndex;
    [ObservableProperty] private bool randomWallpaper;
    [ObservableProperty] private double wallpaperBlur;
    [ObservableProperty] private double wallpaperBrightness;
    [ObservableProperty] private double wallpaperOpacity;
    [ObservableProperty] private string wallpaperStatus = "";
    public AdvancedAppearanceViewModel(AppearanceSettings settings, Localizer text)
    {
        Text = text; settings = settings.Normalize();
        Light = new(settings.Light, text, true); Dark = new(settings.Dark, text, false, Light);
        Fits = new[] { "smart", "center", "fit", "stretch", "tile", "top_left", "top_right" }.Select(id => new ThemeOptionViewModel(id, "Fit_" + id)).ToArray();
        topNavigation = settings.TopNavigation; reduceMotion = settings.ReduceMotion;
        keepWindowOpaque = settings.KeepWindowOpaque;
        darkStart = TimeSpan.FromMinutes(settings.DarkStart); darkEnd = TimeSpan.FromMinutes(settings.DarkEnd);
        latitude = settings.Latitude; longitude = settings.Longitude;
        wallpaperDirectory = settings.WallpaperDirectory;
        wallpaperFile = settings.WallpaperFile; rotateWallpapers = settings.RotateWallpapers; wallpaperIntervalMinutes = settings.WallpaperIntervalMinutes;
        fitIndex = Fits.ToList().FindIndex(f => f.Id == settings.WallpaperFit); randomWallpaper = settings.RandomWallpaper;
        wallpaperBlur = settings.WallpaperBlur; wallpaperBrightness = settings.WallpaperBrightness; wallpaperOpacity = settings.WallpaperOpacity;
        RefreshLabels();
        Light.PropertyChanged += (_, _) => Changed?.Invoke();
        Dark.PropertyChanged += (_, _) => Changed?.Invoke();
        PropertyChanged += (_, e) => { if (e.PropertyName is not (nameof(Profile) or nameof(EditLight) or nameof(WallpaperStatus) or nameof(DesktopOpacityStatus))) Changed?.Invoke(); };
    }
    public void RefreshLabels()
    {
        Light.RefreshLabels(); Dark.RefreshLabels();
        foreach (var fit in Fits) fit.Label = Text[fit.LabelKey];
        OnPropertyChanged(nameof(DesktopOpacityGuide));
    }
    public void Restore(AppearanceSettings settings)
    {
        settings = settings.Normalize();
        Light.Restore(settings.Light); Dark.Restore(settings.Dark);
        TopNavigation = settings.TopNavigation; ReduceMotion = settings.ReduceMotion;
        KeepWindowOpaque = settings.KeepWindowOpaque;
        DarkStart = TimeSpan.FromMinutes(settings.DarkStart); DarkEnd = TimeSpan.FromMinutes(settings.DarkEnd);
        Latitude = settings.Latitude; Longitude = settings.Longitude; WallpaperDirectory = settings.WallpaperDirectory;
        WallpaperFile = settings.WallpaperFile; RotateWallpapers = settings.RotateWallpapers; WallpaperIntervalMinutes = settings.WallpaperIntervalMinutes;
        FitIndex = Fits.ToList().FindIndex(f => f.Id == settings.WallpaperFit); RandomWallpaper = settings.RandomWallpaper;
        WallpaperBlur = settings.WallpaperBlur; WallpaperBrightness = settings.WallpaperBrightness; WallpaperOpacity = settings.WallpaperOpacity;
    }
    public AppearanceSettings Snapshot() => new AppearanceSettings
    {
        Light = Light.Snapshot(), Dark = Dark.Snapshot(), TopNavigation = TopNavigation, ReduceMotion = ReduceMotion,
        KeepWindowOpaque = KeepWindowOpaque,
        DarkStart = (int)DarkStart.TotalMinutes, DarkEnd = (int)DarkEnd.TotalMinutes, Latitude = Latitude, Longitude = Longitude,
        WallpaperDirectory = WallpaperDirectory, WallpaperFit = Fits[Math.Clamp(FitIndex, 0, Fits.Count - 1)].Id,
        WallpaperFile = WallpaperFile, RotateWallpapers = RotateWallpapers, WallpaperIntervalMinutes = WallpaperIntervalMinutes,
        RandomWallpaper = RandomWallpaper, WallpaperBlur = WallpaperBlur, WallpaperBrightness = WallpaperBrightness, WallpaperOpacity = WallpaperOpacity
    }.Normalize();
    public void ChooseWallpaperFile(string path) { RotateWallpapers = false; WallpaperDirectory = ""; WallpaperFile = path; }
    public void ChooseWallpaperDirectory(string path) { WallpaperFile = ""; WallpaperDirectory = path; }
    [RelayCommand] private void ClearWallpaper() { WallpaperFile = ""; WallpaperDirectory = ""; RotateWallpapers = false; }
    [RelayCommand] private void NextWallpaper() => NextWallpaperRequested?.Invoke();
}

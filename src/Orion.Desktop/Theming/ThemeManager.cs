using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Orion.Infrastructure.Storage;

namespace Orion.Desktop.Theming;

public sealed record ThemeChoice(string Id, string LabelKey);
public sealed record ColorPalette(string Id, string LabelKey, string DarkAccent, string LightAccent)
{
    public IBrush Swatch => new SolidColorBrush(Color.Parse(DarkAccent));
}

/// <summary>One semantic palette for all Orion windows and Fluent control states.</summary>
public static class ThemeManager
{
    public static IReadOnlyList<ThemeChoice> Modes { get; } =
        [new("dark", "ThemeDark"), new("light", "ThemeLight"), new("system", "ThemeSystem"), new("schedule", "ThemeSchedule"), new("solar", "ThemeSolar")];
    public static IReadOnlyList<ColorPalette> Palettes { get; } =
    [
        new("mint", "PaletteMint", "#91CCBC", "#236E59"),
        new("blue", "PaletteBlue", "#98BDE8", "#315FA0"),
        new("lavender", "PaletteLavender", "#BEB0E4", "#7051A0"),
        new("amber", "PaletteAmber", "#DEC18E", "#805B20")
    ];

    public static ColorPalette ResolvePalette(string? paletteId, VisualTheme theme) =>
        Palettes.Concat(AccentCatalog.All).FirstOrDefault(p => p.Id == paletteId) ?? Palettes.Single(p => p.Id == theme.AccentId);

    public static AppearanceSettings Current { get; private set; } = new();
    public static event Action? Changed;

    public static void Apply(string? mode, string? paletteId, string? visualThemeId = "orion", AppearanceSettings? appearance = null)
    {
        if (Avalonia.Application.Current is not { } app) return;
        var theme = VisualThemes.Find(visualThemeId);
        Current = (appearance ?? new()).Normalize();
        var fluent = app.Styles.OfType<FluentTheme>().Single();
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var light = variant == ThemeVariant.Light;
            var profile = light ? Current.Light : Current.Dark;
            var palette = profile.Accent == "custom" ? AccentCatalog.FromColor("custom", "Custom", Color.Parse(profile.CustomAccent))
                : ResolvePalette(profile.Accent == "inherit" ? paletteId : profile.Accent, theme);
            var resources = CreateResources(palette, light, theme, profile);
            // Keep brush and dictionary identities stable during live changes from an open selector.
            // Replacing the entire dictionary invalidates templates while input is being dispatched.
            if (app.Resources.ThemeDictionaries.TryGetValue(variant, out var existing) && existing is ResourceDictionary current)
            {
                foreach (var entry in resources)
                    if (current.TryGetValue(entry.Key, out var old) && old is SolidColorBrush oldBrush && entry.Value is SolidColorBrush brush)
                        oldBrush.Color = brush.Color;
                    else current[entry.Key] = entry.Value;
            }
            else app.Resources.ThemeDictionaries[variant] = resources;
            Color Resource(string key) => ((SolidColorBrush)resources["Orion" + key + "Brush"]!).Color;
            fluent.Palettes[variant] = new ColorPaletteResources
            {
                Accent = Resource("Accent"), RegionColor = Resource("Panel"), ChromeLow = Resource("Inset"),
                ChromeMediumLow = Resource("Sidebar"), ChromeMedium = Resource("Border"), ChromeHigh = Resource("Border"),
                BaseHigh = Resource("Text"), BaseMedium = Resource("Muted")
            };
        }
        app.RequestedThemeVariant = ThemeClock.Resolve(mode, Current, DateTimeOffset.Now) switch
        {
            "light" => ThemeVariant.Light,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
        Changed?.Invoke();
    }

    public static ResourceDictionary CreateResources(ColorPalette palette, bool light, VisualTheme? theme = null, AppearanceProfile? profile = null)
    {
        theme ??= VisualThemes.All[0];
        profile = (profile ?? new()).Normalize();
        var surfaces = light ? theme.Light : theme.Dark;
        var accent = Color.Parse(light ? palette.LightAccent : palette.DarkAccent);
        if (profile.BaseMode != "original")
        {
            var neutral = light ? VisualThemes.Find("graphite").Light : VisualThemes.Find("graphite").Dark;
            var seed = profile.BaseMode == "color" ? Color.Parse(profile.BaseColor) : accent;
            var tint = Mix(light ? Colors.White : Colors.Black, seed, light ? .12 : .16);
            string Tint(string hex) => Mix(Color.Parse(hex), tint, profile.BaseMode == "none" ? 0 : profile.BaseOpacity / 100).ToString();
            surfaces = neutral with { Window = Tint(neutral.Window), Panel = Tint(neutral.Panel), Sidebar = Tint(neutral.Sidebar), Inset = Tint(neutral.Inset) };
        }
        var panel = Color.Parse(surfaces.Panel);
        var text = Color.Parse(surfaces.Text);
        var resources = new ResourceDictionary();
        void Set(string key, Color color) => resources["Orion" + key + "Brush"] = new SolidColorBrush(color);
        void Hex(string key, string dark, string bright) => Set(key, Color.Parse(light ? bright : dark));
        Set("Window", Color.Parse(surfaces.Window));
        Set("Sidebar", Color.Parse(surfaces.Sidebar));
        Set("Panel", panel);
        Set("Inset", Color.Parse(surfaces.Inset));
        Set("Border", Color.Parse(surfaces.Border));
        Set("Text", text);
        Set("Muted", Color.Parse(surfaces.Muted));
        Set("Hover", Mix(panel, text, .06));
        // Text links and selected rows use the same accent: validate against their actual surface.
        for (var i = 0; i < 64 && Contrast(accent, Mix(panel, accent, light ? .12 : .15)) < 4.5; i++)
            accent = Mix(accent, light ? Colors.Black : Colors.White, .04);
        Set("Accent", accent);
        Set("AccentHover", Mix(accent, light ? Colors.Black : Colors.White, .12));
        Set("OnAccent", Contrast(accent, Colors.White) >= 4.5 ? Colors.White : Colors.Black);
        Set("AccentSurface", Mix(panel, accent, light ? .08 : .10));
        Set("Selection", Mix(panel, accent, light ? .12 : .15));
        Set("Status", Mix(panel, accent, light ? .10 : .14));
        Set("AccentBorder", Mix(panel, accent, .40));
        Hex("ErrorSurface", "#32252E", "#FCEBF0");
        Hex("ErrorBorder", "#795363", "#C87B94");
        Hex("ErrorText", "#F4B3C4", "#932A4B");
        Hex("WarningSurface", "#332E24", "#FFF3D9");
        Hex("WarningText", "#EBD2A0", "#735311");
        Set("Console", Color.Parse(surfaces.Console));
        Set("ConsoleText", text);
        // A neutral scrim keeps dialogs distinct without tinting the content.
        Hex("Overlay", "#C00B1018", "#80303945");
        if (!profile.ProtectReadability)
        {
            var overlay = light ? Color.Parse("#303945") : Color.Parse("#0B1018");
            Set("Overlay", Color.FromArgb((byte)Math.Round(255 * (.12 + .25 * profile.SurfaceOpacity / 100)), overlay.R, overlay.G, overlay.B));
        }
        // Translucency is opt-in; text/icons keep full opacity on every surface.
        var opacity = profile.ProtectReadability ? Math.Max(.96, profile.SurfaceOpacity / 100) : profile.SurfaceOpacity / 100;
        if (profile.ProtectReadability)
        {
            var muted = Color.Parse(surfaces.Muted);
            while (opacity < 1 && new[] { Colors.Black, Colors.White }.Any(backdrop =>
                Contrast(muted, Mix(backdrop, panel, opacity)) < 4.5 || Contrast(text, Mix(backdrop, panel, opacity)) < 4.5))
                opacity = Math.Min(1, opacity + .005);
        }
        Set("Material", Color.FromArgb((byte)Math.Round(255 * opacity), panel.R, panel.G, panel.B));
        void Material(string key, string color)
        {
            var value = Color.Parse(color);
            Set(key, Color.FromArgb((byte)Math.Round(255 * opacity), value.R, value.G, value.B));
        }
        Material("MaterialSidebar", surfaces.Sidebar);
        Material("MaterialInset", surfaces.Inset);
        // Fluent popup presenters use theme resources instead of Orion's panel style.
        // A menu is above text and controls, not just wallpaper. Keep a readable backplate.
        Set("Popup", Color.FromArgb((byte)Math.Round(255 * Math.Max(.94, opacity)), panel.R, panel.G, panel.B));
        resources["ComboBoxDropDownBackground"] = resources["OrionPopupBrush"];
        resources["MenuFlyoutPresenterBackground"] = resources["OrionPopupBrush"];
        resources["FlyoutPresenterBackground"] = resources["OrionPopupBrush"];
        return resources;
    }

    public static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double F(byte v) => v / 255d <= .04045 ? v / 255d / 12.92 : Math.Pow((v / 255d + .055) / 1.055, 2.4);
            return .2126 * F(c.R) + .7152 * F(c.G) + .0722 * F(c.B);
        }
        var x = L(a); var y = L(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
    public static Color EnsureContrast(Color color, Color background, double ratio)
    {
        var target = Contrast(Colors.Black, background) > Contrast(Colors.White, background) ? Colors.Black : Colors.White;
        for (var i = 0; i < 100 && Contrast(color, background) < ratio; i++) color = Mix(color, target, .06);
        return color;
    }

    private static Color Mix(Color background, Color foreground, double amount) => Color.FromRgb(
        (byte)Math.Round(background.R + (foreground.R - background.R) * amount),
        (byte)Math.Round(background.G + (foreground.G - background.G) * amount),
        (byte)Math.Round(background.B + (foreground.B - background.B) * amount));
}

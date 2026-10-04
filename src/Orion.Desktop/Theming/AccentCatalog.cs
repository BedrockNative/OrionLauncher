using Avalonia.Media;

namespace Orion.Desktop.Theming;

/// <summary>49 additional hues, alongside Orion's four standard palettes.</summary>
public static class AccentCatalog
{
    private const string Seeds = """
        emerald|Emerald|10B981
        blue|Blue|3B82F6
        violet|Violet|8B5CF6
        amber|Amber|F59E0B
        pink|Pink|FF6B8B
        cyan|Cyan|06B6D4
        win_yellow|Yellow|FFB900
        win_orange|Orange|FF8C00
        win_red|Red|D13438
        win_blue|Ocean blue|0078D7
        win_green|Green|10893E
        win_purple|Purple|6B69D6
        win_teal|Teal|0099BC
        win_pink_red|Coral|E74856
        win_light_orange|Light orange|F7630C
        win_orange_red|Burnt orange|CA5010
        win_red_orange|Red orange|DA3B01
        win_light_red|Salmon|EF6950
        win_bright_red|Bright red|FF4343
        win_deep_red|Deep red|E81123
        win_rose|Rose|EA005E
        win_dark_rose|Dark rose|C30052
        win_magenta|Magenta|E3008C
        win_dark_magenta|Dark magenta|BF0077
        win_orchid|Orchid|C239B3
        win_dark_orchid|Dark orchid|9A0089
        win_dark_blue|Deep blue|0063B1
        win_light_purple|Periwinkle|8E8CD8
        win_medium_purple|Medium purple|8764B8
        win_dark_purple|Dark purple|744DA9
        win_light_magenta|Light magenta|B146C2
        win_deep_purple|Deep purple|881798
        win_dark_teal|Dark teal|2D7D9A
        win_cyan|Bright cyan|00B7C3
        win_dark_cyan|Dark cyan|038387
        win_green_blue|Sea green|00B294
        win_dark_green_blue|Dark sea green|018574
        win_light_green|Light green|00CC6A
        win_gray|Warm gray|7A7574
        win_dark_gray|Dark warm gray|5D5A58
        win_blue_gray|Blue gray|68768A
        win_dark_blue_gray|Dark blue gray|515C6B
        win_green_gray|Green gray|567C73
        win_dark_green_gray|Dark green gray|486860
        win_olive|Olive|498205
        win_dark_green|Forest|107C10
        win_medium_gray|Gray|767676
        win_darker_gray|Charcoal|4C4A48
        win_slate_gray|Slate|69797E
        """;
    public static IReadOnlyList<ColorPalette> All { get; } = Seeds.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
    {
        var fields = line.Trim().Split('|');
        // Prefix prevents changing old saved blue/amber palettes.
        return FromColor("hue_" + fields[0], fields[1], Color.Parse("#" + fields[2]));
    }).ToArray();

    public static ColorPalette FromColor(string id, string label, Color seed) => new(id, label,
        ThemeManager.EnsureContrast(seed, Color.Parse("#303030"), 5.5).ToString(),
        ThemeManager.EnsureContrast(seed, Color.Parse("#DADADA"), 5.5).ToString());
}

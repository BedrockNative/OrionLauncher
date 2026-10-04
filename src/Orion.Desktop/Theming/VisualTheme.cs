namespace Orion.Desktop.Theming;

/// <summary>Complete surface sets; accent choice remains independent of light/dark mode.</summary>
public sealed record ThemeSurfaces(string Window, string Sidebar, string Panel, string Inset,
    string Border, string Text, string Muted, string Console);

public sealed record VisualTheme(string Id, string LabelKey, string DescriptionKey, string AccentId,
    ThemeSurfaces Dark, ThemeSurfaces Light);

public static class VisualThemes
{
    public static IReadOnlyList<VisualTheme> All { get; } =
    [
        new("orion", "VisualOrion", "VisualOrionHint", "mint",
            new("#10151E", "#161D27", "#1B232E", "#141B25", "#354152", "#EBF0F7", "#A3AFBF", "#0C111A"),
            new("#F0F3F8", "#E5EBF3", "#FFFFFF", "#F3F6FA", "#C5CFDC", "#202B38", "#526278", "#FAFBFE")),
        new("graphite", "VisualGraphite", "VisualGraphiteHint", "lavender",
            new("#141414", "#1B1B1B", "#232323", "#191919", "#414141", "#EFEFEF", "#B0B0B0", "#101010"),
            new("#F2F2F2", "#E8E8E8", "#FFFFFF", "#F5F5F5", "#CBCBCB", "#252525", "#606060", "#FCFCFC")),
        new("grove", "VisualGrove", "VisualGroveHint", "mint",
            new("#101916", "#17221D", "#1D2B24", "#142019", "#3A5144", "#EAF2EC", "#A7BBAE", "#0C1510"),
            new("#EEF4ED", "#E2ECDF", "#FBFDF8", "#F1F6ED", "#C2D2BE", "#223126", "#506753", "#FAFDF7")),
        new("dune", "VisualDune", "VisualDuneHint", "amber",
            new("#1C1712", "#251F19", "#2D251D", "#211B15", "#554736", "#F5EDE1", "#C0B19E", "#16110D"),
            new("#F6F0E5", "#EDE3D3", "#FFFBF3", "#F7F1E5", "#D6C8B3", "#352A1C", "#71604B", "#FFFCF6"))
    ];

    public static VisualTheme Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
}

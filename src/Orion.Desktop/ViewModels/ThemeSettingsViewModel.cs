using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orion.Desktop.Theming;
using Orion.Desktop.I18n;
using Orion.Infrastructure.Storage;

namespace Orion.Desktop.ViewModels;

public partial class ThemeOptionViewModel(string id, string labelKey, Avalonia.Media.IBrush? swatch = null) : ObservableObject
{
    public string Id { get; } = id;
    public string LabelKey { get; } = labelKey;
    public Avalonia.Media.IBrush? Swatch { get; } = swatch;
    [ObservableProperty] private string label = "";
    public override string ToString() => Label;
}

public partial class VisualThemeOptionViewModel(VisualTheme theme) : ThemeOptionViewModel(theme.Id, theme.LabelKey)
{
    public VisualTheme Theme { get; } = theme;
    [ObservableProperty] private string description = "";
}

public partial class ThemeSettingsViewModel : ObservableObject, IDisposable
{
    private (string Mode, string Palette, string Visual) saved;
    private AppearanceSettings savedAppearance = new();
    private readonly Avalonia.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private string? clockMode;
    private bool restoring;
    private (string Mode, string Palette, string Visual, string? Clock, AppearanceSettings Appearance)? applied;
    public AdvancedAppearanceViewModel Advanced { get; }
    public Localizer Text { get; }
    public IReadOnlyList<ThemeOptionViewModel> Modes { get; } = ThemeManager.Modes.Select(m => new ThemeOptionViewModel(m.Id, m.LabelKey)).ToArray();
    public IReadOnlyList<ThemeOptionViewModel> Palettes { get; } = [new("theme", "PaletteTheme"), .. ThemeManager.Palettes.Select(p => new ThemeOptionViewModel(p.Id, p.LabelKey, p.Swatch))];
    public IReadOnlyList<VisualThemeOptionViewModel> Visuals { get; } = VisualThemes.All.Select(t => new VisualThemeOptionViewModel(t)).ToArray();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty))] private int modeIndex;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty))] private int paletteIndex;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty))] private int visualIndex;
    public ThemeOptionViewModel SelectedMode => Modes[Math.Clamp(ModeIndex, 0, Modes.Count - 1)];
    public ThemeOptionViewModel SelectedPalette => Palettes[Math.Clamp(PaletteIndex, 0, Palettes.Count - 1)];
    public VisualThemeOptionViewModel SelectedVisual => Visuals[Math.Clamp(VisualIndex, 0, Visuals.Count - 1)];
    public bool IsDirty => saved != (SelectedMode.Id, SelectedPalette.Id, SelectedVisual.Id) || savedAppearance != Advanced.Snapshot()
        || !Advanced.Light.ColorsValid || !Advanced.Dark.ColorsValid;
    public bool IsSchedule => SelectedMode.Id == "schedule";
    public bool IsSolar => SelectedMode.Id == "solar";

    public ThemeSettingsViewModel(LauncherSettings settings, Localizer text)
    {
        Text = text;
        RefreshLabels();
        modeIndex = Math.Max(0, Modes.ToList().FindIndex(m => m.Id == settings.ThemeMode));
        paletteIndex = Math.Max(0, Palettes.ToList().FindIndex(p => p.Id == settings.ColorPalette));
        visualIndex = Math.Max(0, Visuals.ToList().FindIndex(p => p.Id == settings.VisualTheme));
        Advanced = new(settings.Appearance ?? new(), text);
        Advanced.Changed += AdvancedChanged;
        timer.Tick += (_, _) =>
        {
            var resolved = ThemeClock.Resolve(SelectedMode.Id, Advanced.Snapshot(), DateTimeOffset.Now);
            if (resolved != clockMode) Apply();
        };
        AcceptChanges();
        Apply();
    }

    partial void OnModeIndexChanged(int value) { OnPropertyChanged(nameof(IsSchedule)); OnPropertyChanged(nameof(IsSolar)); Apply(); }
    partial void OnPaletteIndexChanged(int value) => Apply();
    partial void OnVisualIndexChanged(int value) => Apply();
    private void AdvancedChanged() { Apply(); OnPropertyChanged(nameof(IsDirty)); }
    private void Apply()
    {
        if (Advanced is null || restoring) return;
        var snapshot = Advanced.Snapshot();
        clockMode = ThemeClock.Resolve(SelectedMode.Id, snapshot, DateTimeOffset.Now);
        var next = (SelectedMode.Id, SelectedPalette.Id, SelectedVisual.Id, clockMode, snapshot);
        if (applied == next) return;
        applied = next;
        ThemeManager.Apply(SelectedMode.Id, SelectedPalette.Id, SelectedVisual.Id, snapshot);
        timer.IsEnabled = IsSchedule || IsSolar;
    }
    public void Dispose() { timer.Stop(); Advanced.Changed -= AdvancedChanged; }
    public void AcceptChanges(LauncherSettings? persisted = null)
    {
        // A user may change the preview while an asynchronous save is in progress.
        // Only mark the snapshot actually written to disk as saved.
        saved = persisted is null
            ? (SelectedMode.Id, SelectedPalette.Id, SelectedVisual.Id)
            : (persisted.ThemeMode, persisted.ColorPalette, persisted.VisualTheme);
        savedAppearance = persisted is null ? Advanced.Snapshot() : (persisted.Appearance ?? new()).Normalize();
        OnPropertyChanged(nameof(IsDirty));
    }
    public void RefreshLabels()
    {
        foreach (var option in Modes.Concat(Palettes).Concat(Visuals)) option.Label = Text[option.LabelKey];
        foreach (var option in Visuals) option.Description = Text[option.Theme.DescriptionKey];
        Advanced?.RefreshLabels();
    }

    [RelayCommand] private void Revert()
    {
        restoring = true;
        try
        {
            ModeIndex = Modes.ToList().FindIndex(m => m.Id == saved.Mode);
            PaletteIndex = Palettes.ToList().FindIndex(p => p.Id == saved.Palette);
            VisualIndex = Visuals.ToList().FindIndex(v => v.Id == saved.Visual);
            Advanced.Restore(savedAppearance);
        }
        finally { restoring = false; }
        Apply();
        OnPropertyChanged(nameof(IsDirty));
    }

    [RelayCommand] private void Reset()
    {
        restoring = true;
        try
        {
            ModeIndex = 0;
            PaletteIndex = 0;
            VisualIndex = 0;
            // Desktop exceptions are external state and are undone only through their explicit control.
            Advanced.Restore(new() { KeepWindowOpaque = Advanced.KeepWindowOpaque });
        }
        finally { restoring = false; }
        Apply();
        OnPropertyChanged(nameof(IsDirty));
    }
}

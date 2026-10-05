namespace Orion.Infrastructure.Storage;

public sealed record LauncherSettings(string Language = "en-US", bool KeepInBackground = true,
    string ThemeMode = "dark", string ColorPalette = "theme", string VisualTheme = "orion")
{
    public AppearanceSettings Appearance { get; init; } = new();
    public string FileManager { get; init; } = "system";
    public bool CheckRuntimeUpdatesOnStartup { get; init; } = true;
}

public sealed class SettingsStore(AppPaths paths)
{
    private string FilePath => Path.Combine(paths.Config, "settings.json");
    public async Task<LauncherSettings> LoadAsync(CancellationToken ct = default) =>
        await AtomicFile.ReadJsonAsync<LauncherSettings>(FilePath, ct) ?? new LauncherSettings(
            System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? "pt-BR" : "en-US");
    public Task SaveAsync(LauncherSettings settings, CancellationToken ct = default) => AtomicFile.WriteJsonAsync(FilePath, settings, ct);
}

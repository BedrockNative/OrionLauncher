using System.ComponentModel;
using System.Text.Json;

namespace Orion.Desktop.I18n;

/// <summary>JSON/indexer localization, inspired by JaymeFernandes' Orion translation system.</summary>
public sealed class Localizer : INotifyPropertyChanged
{
    private readonly Dictionary<string, string> english = Read("en-US");
    private Dictionary<string, string> active = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => active.GetValueOrDefault(key) ?? english.GetValueOrDefault(key) ?? key;
    public void SetLanguage(string language)
    {
        active = Read(language);
        PropertyChanged?.Invoke(this, new("Item[]"));
        PropertyChanged?.Invoke(this, new("Item"));
    }
    private static Dictionary<string, string> Read(string language)
    {
        if (language is not ("en-US" or "pt-BR")) language = "en-US";
        var assembly = typeof(Localizer).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith($"I18n.{language}.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}

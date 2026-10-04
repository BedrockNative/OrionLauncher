using System.Text.Json;
using Orion.Domain;

namespace Orion.Infrastructure.Content;

internal static class ContentMetadata
{
    internal static ContentEntry World(string root, string directory, string? profile = null, string? fallbackName = null)
    {
        var nameFile = ContentFiles.Safe(directory, "levelname.txt");
        var name = File.Exists(nameFile) ? ContentFiles.ReadSmall(nameFile, 4096).Trim() : "";
        // Minecraft formatting is not markup in Avalonia; preserve the file, show readable text.
        name = System.Text.RegularExpressions.Regex.Replace(name, "§[0-9a-v]", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant).Trim();
        if (string.IsNullOrWhiteSpace(name)) name = fallbackName ?? Path.GetFileName(directory);
        var level = ContentFiles.Safe(directory, "level.dat");
        var modified = File.Exists(level) ? File.GetLastWriteTimeUtc(level) : Directory.GetLastWriteTimeUtc(directory);
        // Bedrock updates level.dat and LevelDB files, not necessarily the world directory itself.
        foreach (var file in new[] { "level.dat", "levelname.txt", "world_icon.jpeg" })
        {
            var path = ContentFiles.Safe(directory, file);
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > modified) modified = File.GetLastWriteTimeUtc(path);
        }
        var db = ContentFiles.Safe(directory, "db");
        if (Directory.Exists(db))
            foreach (var file in Directory.EnumerateFiles(db))
                if (new FileInfo(file).LinkTarget is null && File.GetLastWriteTimeUtc(file) > modified) modified = File.GetLastWriteTimeUtc(file);
        var icon = ContentFiles.Safe(directory, "world_icon.jpeg");
        return new(Path.GetRelativePath(root, directory), name[..Math.Min(name.Length, 200)], ContentKind.World, "", null, profile)
        {
            ModifiedAt = new DateTimeOffset(modified, TimeSpan.Zero),
            IconPath = File.Exists(icon) && new FileInfo(icon).Length <= 4 * 1024 * 1024 ? icon : null
        };
    }

    internal static string? WorldPlaceholder(string instance)
    {
        // Reuse the user's installed game art; do not redistribute Minecraft assets with Orion.
        var assets = ContentFiles.Safe(instance, "game/data/gui/dist/hbui/assets");
        if (!Directory.Exists(assets)) return null;
        var path = Directory.EnumerateFiles(assets, "world-preview-default-*.jpg").Order().FirstOrDefault();
        return path is not null && new FileInfo(path) is { LinkTarget: null, Length: <= 4 * 1024 * 1024 } ? path : null;
    }

    internal static ContentEntry Pack(string root, string directory, string? profile = null)
    {
        using var doc = JsonDocument.Parse(ContentFiles.ReadSmall(Path.Combine(directory, "manifest.json")),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 32 });
        var header = doc.RootElement.GetProperty("header");
        var uuid = Guid.Parse(header.GetProperty("uuid").GetString()!).ToString();
        var types = doc.RootElement.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("type").GetString()).ToArray();
        var kind = types.Contains("resources") && types.All(t => t == "resources") ? ContentKind.Texture
            : types.Length > 0 && types.All(t => t is "data" or "script") ? ContentKind.Addon
            : throw new InvalidDataException("Only behavior and resource packs are supported.");
        string Version(string name) => header.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.Array ? string.Join('.', value.EnumerateArray()) : value.GetString() ?? "" : "";
        var translations = new Dictionary<string, string>();
        var lang = ContentFiles.Safe(directory, "texts/en_US.lang");
        if (File.Exists(lang))
            foreach (var line in ContentFiles.ReadSmall(lang).Split('\n'))
            {
                var index = line.IndexOf('=');
                if (index > 0 && !line.TrimStart().StartsWith('#')) translations[line[..index].Trim()] = line[(index + 1)..].Split("##")[0].Trim();
            }
        string Text(string key)
        {
            var value = header.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? translations.GetValueOrDefault(v.GetString()!, v.GetString()!) : "";
            return value[..Math.Min(value.Length, key == "name" ? 200 : 4000)];
        }
        var icon = ContentFiles.Safe(directory, "pack_icon.png");
        return new(Path.GetRelativePath(root, directory), Text("name"), kind, Version("version"), uuid, profile)
        {
            Description = Text("description"), MinimumEngineVersion = Version("min_engine_version"),
            IconPath = File.Exists(icon) && new FileInfo(icon).Length <= 4 * 1024 * 1024 ? icon : null
        };
    }
}

using System.Text.Json;
using Orion.Application;
using Orion.Domain;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Games;

public sealed class VersionCatalog(HttpClient http, AppPaths paths) : IVersionCatalog
{
    public const string Source = "https://raw.githubusercontent.com/LukasPAH/minecraft-windows-gdk-version-db/refs/heads/main/historical_versions.json";
    public bool UsedCachedData { get; private set; }

    public async Task<IReadOnlyList<GameVersion>> GetAsync(CancellationToken cancellationToken = default)
    {
        var cache = Path.Combine(paths.Cache, "versions.json");
        UsedCachedData = false;
        try
        {
            var json = await http.GetStringAsync(Source, cancellationToken);
            var versions = Parse(json);
            await AtomicFile.WriteAsync(cache, json, cancellationToken);
            return versions;
        }
        catch (Exception ex) when (File.Exists(cache) && (ex is HttpRequestException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            UsedCachedData = true;
            return Parse(await File.ReadAllTextAsync(cache, cancellationToken));
        }
    }

    public static IReadOnlyList<GameVersion> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var versions = new List<GameVersion>();
        foreach (var (property, channel) in new[] { ("releaseVersions", "Release"), ("previewVersions", "Preview") })
        {
            if (!doc.RootElement.TryGetProperty(property, out var rows)) continue;
            foreach (var row in rows.EnumerateArray())
            {
                var label = row.GetProperty("version").GetString()!.Replace(channel + " ", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (!Version.TryParse(label, out _)) continue;
                foreach (var candidate in row.GetProperty("urls").EnumerateArray())
                    if (Uri.TryCreate(candidate.GetString(), UriKind.Absolute, out var url) && GamePackageSource.IsSupported(url))
                    { versions.Add(new(label, channel, url)); break; }
            }
        }
        if (versions.Count == 0) throw new InvalidDataException("The version catalog contains no supported GDK packages.");
        return versions.OrderByDescending(v => Version.Parse(v.Version)).ToArray();
    }
}

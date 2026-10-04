using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orion.Application;
using Orion.Domain;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Rtx;

/// <summary>Public catalog contracts only; no credentials, scripts or third-party implementation code.</summary>
public sealed partial class RtxCatalog(HttpClient http, IReleaseClient releases, string cache, HttpClient? releaseDownloads = null) : IRtxCatalog
{
    public bool UsedCache { get; private set; }
    public static bool IsAssetUri(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.Host is "bedrock.graphics" or "cdn.bedrock.graphics";
    public static readonly string[] MaterialNames = ["RTXStub", "RTXPostFX", "RTXPostFX.Bloom", "RTXPostFX.Tonemapping"];
    [GeneratedRegex(@"/presets/(?:base/)?v?(\d+(?:\.\d+)+)/", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPath();

    public static IReadOnlyList<RtxPreset> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() > 2000)
            throw new InvalidDataException("Invalid BetterRTX catalog.");
        List<RtxPreset> result = [];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            string? Read(string key) => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var id = Read("uuid"); var name = Read("name");
            if (id is not { Length: > 0 and <= 100 } || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
                || name is not { Length: > 0 and <= 200 } || !ids.Add(id)) continue;
            Dictionary<string, Uri> files = [];
            foreach (var (field, material) in new[] { ("stub", "RTXStub"), ("bloom", "RTXPostFX.Bloom"), ("tonemapping", "RTXPostFX.Tonemapping") })
                if (Uri.TryCreate(Read(field), UriKind.Absolute, out var uri) && IsAssetUri(uri)) files.Add(material, uri);
            if (files.Count != 3) continue;
            var version = Read("gameVersion");
            var inferred = false;
            var versions = files.Values.Select(u => VersionPath().Match(u.AbsolutePath)).ToArray();
            if (string.IsNullOrWhiteSpace(version))
            {
                if (versions.All(m => m.Success) && versions.Select(m => m.Groups[1].Value).Distinct().Count() == 1)
                { version = versions[0].Groups[1].Value; inferred = true; }
            }
            else if (versions.Any(m => m.Success && Compatibility(version, m.Groups[1].Value) != RtxCompatibility.Matching))
                version = null; // Conflicting metadata must not select a convenient target.
            var image = Uri.TryCreate(Read("icon"), UriKind.Absolute, out var icon) && IsAssetUri(icon) ? icon.AbsoluteUri : null;
            result.Add(new(id, name, version, Read("tag"), image, files, inferred));
        }
        if (result.Count == 0) throw new InvalidDataException("The BetterRTX catalog contains no usable presets.");
        return result;
    }

    public async Task<IReadOnlyList<RtxPreset>> PresetsAsync(CancellationToken ct)
    {
        var file = Path.Combine(cache, "catalog.json"); UsedCache = false;
        try
        {
            var json = Encoding.UTF8.GetString(await ReadAsync(new("https://bedrock.graphics/api"), 2 * 1024 * 1024, null, ct));
            var result = Parse(json);
            await AtomicFile.WriteAsync(file, json, ct);
            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            if (!File.Exists(file)) throw;
            var result = Parse(Content.ContentFiles.ReadSmall(file, 2 * 1024 * 1024)); UsedCache = true; return result;
        }
    }

    public async Task<IReadOnlyList<RtxTexturePack>> TexturesAsync(CancellationToken ct)
    {
        var release = await releases.GetLatestAsync(new("Cubeir", "Vanilla-RTX"), ct);
        return release.Assets.Where(a => a.Name.StartsWith("Vanilla-RTX-", StringComparison.Ordinal)
                && a.Name.EndsWith(".mcpack", StringComparison.OrdinalIgnoreCase) && a.Size is > 0 and <= 128 * 1024 * 1024)
            .Select(a => new RtxTexturePack(a.Name[..^7].Replace('-', ' '), release.Tag, a, TexturePackId(a.Name))).ToArray();
    }

    // Stable header UUIDs from Cubeir/Vanilla-RTX's official manifests, not display names.
    // Also recognizes packs installed before Orion tracked RTX installation state.
    public static string? TexturePackId(string assetName) => assetName switch
    {
        var n when n.StartsWith("Vanilla-RTX-Opus-", StringComparison.Ordinal) => "7c87f859-4d79-4d51-8887-bf450b2b2bfa",
        var n when n.StartsWith("Vanilla-RTX-Normals-", StringComparison.Ordinal) => "bbe2b225-b45b-41c2-bd3b-465cd83e6071",
        var n when n.Length > 12 && n.StartsWith("Vanilla-RTX-", StringComparison.Ordinal) && char.IsAsciiDigit(n[12]) => "a5c3cc7d-1740-4b5e-ae2c-71bc14b3f63b",
        _ => null
    };

    public async Task DownloadPresetAsync(RtxPreset preset, string directory, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        if (preset.Materials.Count is < 1 or > 4) throw new InvalidDataException("Invalid shader list.");
        Directory.CreateDirectory(directory);
        var completed = 0;
        foreach (var (name, uri) in preset.Materials)
        {
            if (!MaterialNames.Contains(name) || !IsAssetUri(uri)) throw new InvalidDataException("Untrusted shader URL or material.");
            var bytes = await ReadAsync(uri, 64 * 1024 * 1024, null, ct);
            ValidateMaterial(bytes);
            await File.WriteAllBytesAsync(Path.Combine(directory, name + ".material.bin"), bytes, ct);
            progress?.Report(new(preset.Name, (double)++completed / preset.Materials.Count));
        }
    }

    public async Task DownloadTextureAsync(RtxTexturePack pack, string path, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var uri = pack.Asset.Download;
        if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/Cubeir/Vanilla-RTX/releases/download/", StringComparison.Ordinal)
            || pack.Asset.Size is <= 0 or > 128 * 1024 * 1024) throw new InvalidDataException("Untrusted texture download.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await new AssetDownloader(releaseDownloads ?? http).DownloadAsync(pack.Asset, path, progress, timeout.Token);
    }

    private async Task<byte[]> ReadAsync(Uri uri, int maximum, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("OrionLauncher/0.6");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } final || !IsAssetUri(final)) throw new InvalidDataException("Untrusted RTX redirect.");
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("RTX download exceeds its size limit.");
        using var memory = new MemoryStream();
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[65536]; int count;
        while ((count = await source.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (memory.Length + count > maximum) throw new InvalidDataException("RTX download exceeds its size limit.");
            memory.Write(buffer, 0, count);
        }
        if (memory.Length == 0 || response.Content.Headers.ContentLength is { } expected && memory.Length != expected)
            throw new InvalidDataException("Incomplete RTX download.");
        return memory.ToArray();
    }

    public static void ValidateMaterial(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 16 or > 64 * 1024 * 1024 || !bytes[..4].SequenceEqual(new byte[] { 0x1a, 0xda, 0x11, 0x0a }))
            throw new InvalidDataException("Invalid or empty compiled shader.");
    }

    public static RtxCompatibility Compatibility(string? target, string game)
    {
        static int[]? ParseVersion(string? value)
        {
            var parts = value?.TrimStart('v', 'V').Split('.');
            if (parts is null || parts.Length is < 2 or > 4 || parts.Any(p => p.Length == 0 || p.Any(c => !char.IsAsciiDigit(c)) || !int.TryParse(p, out var n) || n < 0)) return null;
            var numbers = parts.Select(int.Parse).ToArray();
            return numbers.Length >= 3 && numbers[0] == 1 ? numbers[1..] : numbers;
        }
        var a = ParseVersion(target); var b = ParseVersion(game);
        if (a is null || b is null) return RtxCompatibility.Unknown;
        var compared = 0;
        for (var i = 0; i < Math.Max(a.Length, b.Length) && compared == 0; i++)
            compared = (i < b.Length ? b[i] : 0).CompareTo(i < a.Length ? a[i] : 0);
        return compared == 0 ? RtxCompatibility.Matching : compared > 0 ? RtxCompatibility.NewerGame : RtxCompatibility.OlderGame;
    }
}

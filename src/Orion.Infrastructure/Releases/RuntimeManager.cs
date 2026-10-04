using System.Runtime.InteropServices;
using Orion.Application;
using Orion.Domain;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Releases;

public sealed record RuntimeDefinition(string Name, Repository Repository, string AssetPrefix, string[] Executables)
{
    public static RuntimeDefinition Xodus { get; } = new("xodus", new("BedrockNative", "xodus"), "xodus-", ["xodus-cli", "xodus-service"]);
    public static RuntimeDefinition WineGdk { get; } = new("winegdk", new("BedrockNative", "WineGDK"), "wine-", ["wine", "wineboot", "wineserver"]);
}

public sealed record RuntimeUpdate(string Name, string? InstalledTag, string LatestTag)
{
    public bool Available => !string.Equals(InstalledTag?.TrimStart('v'), LatestTag.TrimStart('v'), StringComparison.Ordinal);
}

public sealed class RuntimeManager(AppPaths paths, IReleaseClient releases, AssetDownloader downloader, string? bundledRoot = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    // Startup checks only inspect metadata; runtime downloads remain explicit.
    public async Task<RuntimeUpdate> CheckForUpdatesAsync(RuntimeDefinition definition, CancellationToken ct)
    {
        var release = await releases.GetLatestAsync(definition.Repository, ct);
        SelectAsset(definition, release);
        var installed = await GetInstalledAsync(definition, ct);
        return new(definition.Name, installed?.Tag, release.Tag);
    }

    private async Task<RuntimeInstallation?> BundledAsync(RuntimeDefinition definition, CancellationToken ct)
    {
        if (bundledRoot is null) return null;
        var root = Path.Combine(bundledRoot, definition.Name);
        var tag = await AtomicFile.ReadJsonAsync<string>(Path.Combine(root, "version.json"), ct);
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var installed = new RuntimeInstallation(definition.Name, tag, root);
        return Valid(definition, installed) ? installed : throw new InvalidDataException($"Invalid bundled {definition.Name} runtime.");
    }

    public async Task<RuntimeInstallation?> GetInstalledAsync(RuntimeDefinition definition, CancellationToken ct)
    {
        var installed = await AtomicFile.ReadJsonAsync<RuntimeInstallation>(Path.Combine(paths.Tools, definition.Name, "current.json"), ct);
        return installed is not null && Valid(definition, installed) ? installed : await BundledAsync(definition, ct);
    }

    public async Task<RuntimeInstallation> EnsureAsync(RuntimeDefinition definition, IProgress<OperationProgress>? progress, CancellationToken ct, bool checkForUpdates = false)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The current BedrockNative runtime assets require Linux x86_64.");
        await gate.WaitAsync(ct);
        try
        {
            var root = Path.Combine(paths.Tools, definition.Name);
            var installed = await AtomicFile.ReadJsonAsync<RuntimeInstallation>(Path.Combine(root, "current.json"), ct);
            var bundled = await BundledAsync(definition, ct);
            // Portable releases already contain the tested stack. Do not require a
            // network request or duplicate download on first launch. Updates remain explicit.
            if (!checkForUpdates && bundled is not null)
                return installed is not null && Valid(definition, installed) ? installed : bundled;
            if (installed is null || !Valid(definition, installed)) installed = bundled;
            Release release;
            try { release = await releases.GetLatestAsync(definition.Repository, ct); }
            catch (HttpRequestException) when (installed is not null && Valid(definition, installed))
            {
                progress?.Report(new($"Offline: using installed {definition.Name} {installed.Tag}"));
                return installed;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && installed is not null && Valid(definition, installed))
            {
                progress?.Report(new($"Update check timed out: using {definition.Name} {installed.Tag}"));
                return installed;
            }
            var asset = SelectAsset(definition, release);
            var target = Path.Combine(root, asset.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var candidate = new RuntimeInstallation(definition.Name, release.Tag, target);
            if (!Valid(definition, candidate))
            {
                if (Directory.Exists(target)) throw new InvalidDataException($"Incomplete runtime at {target}; move it aside and retry.");
                // Stable asset identity lets a process crash resume the same validated download.
                var stage = Path.Combine(root, $".install-{asset.Id}");
                Directory.CreateDirectory(stage);
                try
                {
                    var archive = Path.Combine(stage, "download.tar.gz");
                    await downloader.DownloadAsync(asset, archive, progress, ct);
                    var extracted = Path.Combine(stage, "files");
                    if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
                    progress?.Report(new($"Extracting {definition.Name}"));
                    await Task.Run(() => TarArchive.ExtractAsync(archive, extracted, ct), ct);
                    foreach (var executable in definition.Executables) FindExecutable(extracted, executable);
                    Directory.Move(extracted, target);
                }
                finally { Directory.Delete(stage, recursive: true); }
            }
            await AtomicFile.WriteJsonAsync(Path.Combine(root, "current.json"), candidate, ct);
            return candidate;
        }
        finally { gate.Release(); }
    }

    public static ReleaseAsset SelectAsset(RuntimeDefinition definition, Release release)
    {
        var assets = release.Assets.Where(a => a.Name.StartsWith(definition.AssetPrefix, StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            && !a.Name.Contains("aarch64", StringComparison.OrdinalIgnoreCase)
            && !a.Name.Contains("arm64", StringComparison.OrdinalIgnoreCase)
            && !a.Name.Contains("windows", StringComparison.OrdinalIgnoreCase)
            && !a.Name.Contains("macos", StringComparison.OrdinalIgnoreCase)
            && !a.Name.Contains("source", StringComparison.OrdinalIgnoreCase)).ToArray();
        return assets.Length == 1 ? assets[0] : throw new InvalidDataException($"Expected one Linux x64 {definition.Name} archive in release {release.Tag}; found {assets.Length}.");
    }

    public static string FindExecutable(string root, string name)
    {
        if (!Directory.Exists(root)) throw new FileNotFoundException($"Runtime directory missing: {root}");
        // A Wine distribution also has lib/wine/<arch>/wine; its public entry point is bin/wine.
        var candidates = new[] { Path.Combine(root, "bin", name), Path.Combine(root, name) }.Where(File.Exists).ToArray();
        if (candidates.Length == 0)
            candidates = Directory.EnumerateDirectories(root).SelectMany(folder => new[]
                { Path.Combine(folder, "bin", name), Path.Combine(folder, name) }).Where(File.Exists).ToArray();
        var path = candidates.Length == 1 ? candidates[0] : throw new InvalidDataException($"Expected one public {name} executable in {root}.");
        if ((File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            throw new InvalidDataException($"Runtime executable lacks execute permission: {name}");
        return path;
    }

    private static bool Valid(RuntimeDefinition definition, RuntimeInstallation installation)
    {
        try { foreach (var name in definition.Executables) FindExecutable(installation.Directory, name); return true; }
        catch (IOException) { return false; }
    }
}

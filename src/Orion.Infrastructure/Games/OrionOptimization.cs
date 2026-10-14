using System.Reflection.PortableExecutable;
using Orion.Application;
using Orion.Domain;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Games;

public sealed class OrionOptimization(AppPaths paths, IReleaseClient releases, AssetDownloader downloader,
    Repository? repository = null)
{
    public static Repository DefaultRepository { get; } = new("BedrockNative", "Orion.Optimization");
    private readonly SemaphoreSlim gate = new(1, 1);

    public static ReleaseAsset SelectAsset(Release release)
    {
        var assets = release.Assets.Where(a => a.Name.Equals("Orion.Optimization.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
        return assets.Length == 1 ? assets[0] : throw new InvalidDataException($"Release {release.Tag} must contain one Orion.Optimization.dll asset.");
    }

    public static void ValidateDll(string path)
    {
        using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus
            || !pe.PEHeaders.IsDll || pe.PEHeaders.PEHeader.ExportTableDirectory.Size == 0)
            throw new InvalidDataException("Invalid x64 Orion.Optimization DLL.");
    }

    public async Task<string> PrepareAsync(Guid instanceId, string executable, IProgress<OperationProgress>? progress, CancellationToken ct, Func<CancellationToken, Task<byte[]>>? readSource = null)
    {
        await gate.WaitAsync(ct);
        try {
            var root = Path.Combine(paths.Tools, "orion-optimization");
            var currentFile = Path.Combine(root, "current.json");
            var installed = await AtomicFile.ReadJsonAsync<string>(currentFile, ct);
            // Only trust installations inside the managed directory.
            string? dll = installed is not null && long.TryParse(installed, out var id) && id > 0
                ? Path.Combine(root, installed, "Orion.Optimization.dll") : null;
            if (dll is not null) { try { ValidateDll(dll); } catch (Exception e) when (e is IOException or BadImageFormatException) { dll = null; } }
            Release? release = null;
            try { release = await releases.GetLatestAsync(repository ?? DefaultRepository, ct); }
            catch (HttpRequestException e) when (dll is null && e.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException("Orion optimizations are not available for download yet. Disable this option to launch without them.", e);
            }
            catch (HttpRequestException) when (dll is not null) { progress?.Report(new("Offline: using installed Orion.Optimization")); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && dll is not null) { progress?.Report(new("Update check timed out: using installed Orion.Optimization")); }
            if (release is not null) {
                var asset = SelectAsset(release);
                if (asset.Id <= 0 || asset.Size is <= 0 or > 128 * 1024 * 1024) throw new InvalidDataException("Invalid optimization release asset.");
                var key = asset.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var target = Path.Combine(root, key, "Orion.Optimization.dll");
                if (!File.Exists(target)) {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var temporary = target + ".download";
                    progress?.Report(new($"Downloading Orion.Optimization {release.Tag}"));
                    try {
                        await downloader.DownloadAsync(asset, temporary, progress, ct);
                        ValidateDll(temporary); File.Move(temporary, target);
                    } finally {
                        if (File.Exists(temporary)) File.Delete(temporary);
                        if (File.Exists(temporary + ".json")) File.Delete(temporary + ".json");
                    }
                }
                ValidateDll(target); dll = target;
                await AtomicFile.WriteJsonAsync(currentFile, key, ct);
            }
            if (dll is null) throw new IOException("No Orion.Optimization release is available.");
            File.Delete(Path.Combine(paths.Instance(instanceId), "launch-optimized-wine"));
            return await OptimizationExecutable.PrepareAsync(executable, dll, ct, readSource);
        } finally { gate.Release(); }
    }
}

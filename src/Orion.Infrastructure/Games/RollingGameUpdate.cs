using Orion.Application;
using Orion.Domain;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Games;

public interface IGameUpdateInstaller
{
    Task PrepareUpdateAsync(GameInstance instance, string source, IProgress<OperationProgress>? progress, CancellationToken ct);
}

/// <summary>Called off the UI thread, with the instance activity lease held throughout launch.</summary>
public sealed class RollingGameUpdate(AppPaths paths, IVersionCatalog catalog, IGameUpdateInstaller installer)
{
    public async Task<GameInstance> PrepareAsync(GameInstance instance, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var root = paths.Instance(instance.Id);
        var next = Path.Combine(root, ".update-next");
        var backup = Path.Combine(root, ".update-previous");
        var game = paths.Game(instance.Id);
        foreach (var path in new[] { root, next, backup, game })
            if ((Directory.Exists(path) || File.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Game update paths must not be symbolic links.");

        // Once the old game is moved, finish the transaction even after interruption.
        if (Directory.Exists(backup)) instance = await CommitAsync(instance, next, backup, game);
        if (!instance.TrackUpdates && instance.TargetVersion is null) return instance;
        if (!instance.TrackUpdates && Version.Parse(instance.TargetVersion!) <= Version.Parse(instance.Version))
        {
            instance = instance with { TargetVersion = null };
            await new InstanceRepository(paths).SaveAsync(instance, ct);
            return instance;
        }
        progress?.Report(new(instance.TrackUpdates
            ? $"Checking for {(instance.Channel == "Release" ? "Latest Release" : "Latest Preview")} updates"
            : $"Preparing the update to Minecraft {instance.TargetVersion}"));
        IReadOnlyList<GameVersion> versions;
        try { versions = await catalog.GetAsync(ct); }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            progress?.Report(new("Update check unavailable; using the installed game"));
            return instance;
        }
        if (catalog is VersionCatalog { UsedCachedData: true })
        {
            progress?.Report(new("Update check unavailable; using the installed game"));
            return instance;
        }
        // Only the instance's own channel is considered, and never a version older than the installed one.
        var upgrades = GameVersion.UpgradesFor(instance, versions);
        var target = instance.TrackUpdates ? upgrades.FirstOrDefault()
            : upgrades.FirstOrDefault(v => Version.Parse(v.Version) == Version.Parse(instance.TargetVersion!));
        if (target is null)
        {
            if (!instance.TrackUpdates)
                progress?.Report(new($"Minecraft {instance.TargetVersion} is not in the {instance.Channel} catalog; using the installed game"));
            return instance;
        }
        progress?.Report(new($"Updating Minecraft {instance.Version} → {target.Version}"));
        if (Directory.Exists(next)) Directory.Delete(next, true);
        var updated = instance with { Version = target.Version, TargetVersion = null };
        await installer.PrepareUpdateAsync(updated, target.Download.AbsoluteUri, progress, ct);
        GameLayout.Validate(Path.Combine(next, "game"));
        ct.ThrowIfCancellationRequested();
        Directory.Move(game, backup);
        return await CommitAsync(instance, next, backup, game);
    }

    private async Task<GameInstance> CommitAsync(GameInstance original, string next, string backup, string game)
    {
        var updated = await AtomicFile.ReadJsonAsync<GameInstance>(Path.Combine(next, "instance.json"))
            ?? throw new InvalidDataException("Missing game update transaction metadata; previous game is preserved.");
        if (updated.Id != original.Id || updated.Channel != original.Channel)
            throw new InvalidDataException("Game update transaction identity mismatch.");
        if (!Directory.Exists(game)) Directory.Move(Path.Combine(next, "game"), game);
        GameLayout.Validate(game);
        await new InstanceRepository(paths).SaveAsync(updated);
        // Keep metadata until backup cleanup completes so interruption can be recovered.
        Directory.Delete(backup, true);
        Directory.Delete(next, true);
        // Update downloads are not owned by the creation queue. Release only the
        // package and its resume metadata after a successful commit.
        foreach (var file in new[] { "package.msixvc", "package.msixvc.json" })
        {
            try { File.Delete(Path.Combine(paths.Download(original.Id), file)); }
            catch (IOException) { /* A leftover encrypted download is safe to retry/clean later. */ }
            catch (UnauthorizedAccessException) { }
        }
        return updated;
    }
}

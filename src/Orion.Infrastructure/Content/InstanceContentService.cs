using System.IO.Compression;
using System.Text.Json;
using Orion.Domain;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Content;

/// <summary>GDK content only. Never accesses system Minecraft data or edits a world's database.</summary>
public sealed class InstanceContentService(AppPaths paths, InstanceActivity activity)
{
    private sealed record Move(string Source, string Destination);
    private sealed record Receipt(ContentEntry Entry);
    private string Root(Guid id)
    {
        var root = ContentFiles.Safe(paths.Instance(id));
        return Directory.Exists(root) ? root : throw new DirectoryNotFoundException("Instance directory not found.");
    }

    private static List<(string Path, string Label)> UserRoots(string root)
    {
        List<(string, string)> result = [];
        var wineUsers = ContentFiles.Safe(root, "prefix/drive_c/users");
        if (!Directory.Exists(wineUsers)) return result;
        foreach (var user in Directory.EnumerateDirectories(wineUsers))
        foreach (var edition in new[] { "Minecraft Bedrock", "Minecraft Bedrock Preview" })
        {
            var relative = Path.GetRelativePath(root, Path.Combine(user, "AppData", "Roaming", edition, "Users"));
            var users = ContentFiles.Safe(root, relative);
            if (Directory.Exists(users)) result.Add((users, $"{Path.GetFileName(user)} · {edition}"));
        }
        return result;
    }

    private static IReadOnlyList<ContentProfile> Profiles(string root)
    {
        List<ContentProfile> profiles = [];
        foreach (var users in UserRoots(root))
        foreach (var player in Directory.EnumerateDirectories(users.Path))
        {
            if (Path.GetFileName(player) == "Shared") continue;
            var relative = Path.GetRelativePath(root, Path.Combine(player, "games/com.mojang"));
            ContentFiles.Safe(root, relative);
            profiles.Add(new(relative, $"{Path.GetFileName(player)} · {users.Label}"));
        }
        return profiles.OrderBy(p => p.Label).ToArray();
    }

    private static string Shared(string root)
    {
        var roots = UserRoots(root);
        if (roots.Count != 1) throw new InvalidOperationException("Start this instance once first. A single Minecraft data location is required to import packs.");
        return ContentFiles.Safe(root, Path.GetRelativePath(root, Path.Combine(roots[0].Path, "Shared/games/com.mojang")));
    }

    private static string Folder(ContentKind kind) => kind switch
    { ContentKind.World => "minecraftWorlds", ContentKind.Addon => "behavior_packs", ContentKind.Texture => "resource_packs", _ => throw new InvalidDataException("Unknown content kind.") };

    internal static string Destination(string root, ContentKind kind, string? profile)
    {
        if (kind != ContentKind.World) return ContentFiles.Safe(Shared(root), Folder(kind));
        if (profile is null || Profiles(root).All(p => p.Id != profile))
            throw new InvalidOperationException("Choose an existing in-game storage profile for this world. Start and sign into the game first if none exists.");
        return ContentFiles.Safe(root, Path.Combine(profile, Folder(kind)));
    }

    private static ContentEntry Pack(string root, string directory, string? profile = null)
        => ContentMetadata.Pack(root, directory, profile);

    private static ContentEntry Read(string root, string directory, ContentKind kind, string? profile)
    {
        try
        {
            if (kind != ContentKind.World) return Pack(root, directory, profile) with { Kind = kind };
            return ContentMetadata.World(root, directory, profile) with { FallbackIconPath = ContentMetadata.WorldPlaceholder(root) };
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { return new(Path.GetRelativePath(root, directory), Path.GetFileName(directory), kind, "", null, profile, Warning: "Invalid or unreadable metadata"); }
    }

    private List<ContentEntry> Entries(string root, IReadOnlyList<ContentProfile> profiles)
    {
        List<ContentEntry> entries = [];
        void Add(string parent, ContentKind kind, string? profile)
        {
            ContentFiles.Safe(root, Path.GetRelativePath(root, parent));
            if (!Directory.Exists(parent)) return;
            foreach (var directory in Directory.EnumerateDirectories(parent))
            {
                var resolved = SharedContentLinks.Resolve(paths, directory);
                entries.Add(Read(resolved == directory ? root : resolved, resolved, kind, profile) with
                    { Id = Path.GetRelativePath(root, directory), Shared = resolved != directory });
            }
        }
        foreach (var users in UserRoots(root))
        foreach (var kind in new[] { ContentKind.Addon, ContentKind.Texture })
            Add(Path.Combine(users.Path, "Shared/games/com.mojang", Folder(kind)), kind, null);
        foreach (var profile in profiles)
            Add(Path.Combine(root, profile.Id, Folder(ContentKind.World)), ContentKind.World, profile.Id);
        var trash = ContentFiles.Safe(root, ".content-trash");
        if (Directory.Exists(trash))
            foreach (var directory in Directory.EnumerateDirectories(trash))
            {
                ContentFiles.Safe(trash, Path.GetFileName(directory));
                if (!File.Exists(ContentFiles.Safe(directory, "receipt.json"))) continue;
                var receipt = ReadReceipt(root, directory);
                if (Directory.Exists(ContentFiles.Safe(directory, "content")))
                    entries.Add(receipt.Entry with { Id = Path.GetRelativePath(root, directory), Archived = true });
            }
        return entries;
    }

    private static Receipt ReadReceipt(string root, string directory)
    {
        ContentFiles.Safe(root, Path.GetRelativePath(root, directory));
        var receipt = JsonSerializer.Deserialize<Receipt>(ContentFiles.ReadSmall(Path.Combine(directory, "receipt.json")))
            ?? throw new InvalidDataException("Invalid content archive receipt.");
        ValidateDestination(root, receipt.Entry.Id);
        return receipt;
    }

    internal static void ValidateDestination(string root, string relative, bool allowManagedLeaf = false)
    {
        var path = allowManagedLeaf ? Path.Combine(ContentFiles.Safe(root, Path.GetDirectoryName(relative) ?? ""), Path.GetFileName(relative)) : ContentFiles.Safe(root, relative);
        var parents = Profiles(root).Select(p => Path.Combine(root, p.Id, "minecraftWorlds"))
            .Concat(UserRoots(root).SelectMany(u => new[] { "behavior_packs", "resource_packs" }
                .Select(folder => Path.Combine(u.Path, "Shared/games/com.mojang", folder))));
        if (!parents.Contains(Path.GetDirectoryName(path), StringComparer.Ordinal))
            throw new InvalidDataException("Not an instance content directory.");
    }

    public Task<IReadOnlyList<ContentProfile>> StorageFoldersAsync(Guid id, CancellationToken ct = default) => Task.Run<IReadOnlyList<ContentProfile>>(() =>
    {
        ct.ThrowIfCancellationRequested();
        var root = Root(id);
        IReadOnlyList<ContentProfile> Discover()
        {
            var folders = UserRoots(root).Select(users => new ContentProfile(
                ContentFiles.Safe(root, Path.GetRelativePath(root, Path.Combine(users.Path, "Shared/games/com.mojang"))), "Shared · " + users.Label)).ToList();
            folders.AddRange(Profiles(root).Select(profile => new ContentProfile(ContentFiles.Safe(root, profile.Id), profile.Label)));
            return folders.Where(f => Directory.Exists(f.Id)).ToArray();
        }
        // Existing storage can be opened while playing, without mutating the prefix.
        var existing = Discover(); if (existing.Count > 0) return existing;
        using var lease = activity.Acquire(id);
        InstanceContentDirectories.Ensure(root);
        return Discover();
    }, ct);

    public Task<ContentSnapshot> ListAsync(Guid id, CancellationToken ct = default) => Task.Run(() =>
    {
        using var lease = activity.Acquire(id); ct.ThrowIfCancellationRequested();
        var root = Root(id); Recover(root);
        InstanceContentDirectories.Ensure(root);
        var profiles = Profiles(root);
        return new ContentSnapshot(profiles, Entries(root, profiles).OrderBy(e => e.Name).ToArray());
    }, ct);

    // Called under the same instance lease before playing and before any content operation.
    public void Recover(Guid id) { ContentLibraryService.RequireRecovered(paths, id); var root = Root(id); Recover(root); InstanceContentDirectories.Ensure(root); }
    private static void Recover(string root)
    {
        var work = ContentFiles.Safe(root, ".content-work");
        if (!Directory.Exists(work)) return;
        foreach (var transaction in Directory.EnumerateDirectories(work))
        {
            ContentFiles.Safe(work, Path.GetFileName(transaction));
            var journal = ContentFiles.Safe(transaction, "moves.json");
            if (File.Exists(journal) && !File.Exists(ContentFiles.Safe(transaction, "committed")))
            {
                var moves = JsonSerializer.Deserialize<Move[]>(ContentFiles.ReadSmall(journal)) ?? [];
                foreach (var move in moves.Reverse())
                {
                    ValidateDestination(root, move.Destination);
                    var source = ContentFiles.Safe(transaction, move.Source);
                    var dest = ContentFiles.Safe(root, move.Destination);
                    if (!Directory.Exists(source) && Directory.Exists(dest))
                    { Directory.CreateDirectory(Path.GetDirectoryName(source)!); Directory.Move(dest, source); }
                }
            }
            ContentFiles.DeleteWork(transaction);
        }
    }

    public Task ImportAsync(Guid id, string file, string? profileId, CancellationToken ct = default) => ImportCheckedAsync(id, file, profileId, null, ct);

    internal Task ImportCheckedAsync(Guid id, string file, string? profileId, Action<string>? validate, CancellationToken ct) => Task.Run(() =>
    {
        using var lease = activity.Acquire(id);
        var root = Root(id); Recover(root);
        validate?.Invoke(root);
        InstanceContentDirectories.Ensure(root);
        var work = ContentFiles.Safe(root, ".content-work/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var imports = ContentArchive.Extract(file, work, ct);
            Rtx.RtxFamilyPolicy.RequireVanillaImport(root, imports.Select(p => p.Uuid));
            foreach (var world in imports.Where(p => p.Kind == ContentKind.World)) Rtx.RtxFamilyPolicy.RequireWorldImport(root, world.Path);
            if (imports.Any(p => p.Kind != ContentKind.World))
            {
                var existing = Entries(root, Profiles(root)).Where(e => !e.Archived && e.PackId is not null).Select(e => e.PackId).ToHashSet();
                foreach (var pack in imports)
                    if (!existing.Add(pack.Uuid)) throw new InvalidDataException("A pack with this UUID is already installed or duplicated in the archive. Archive the old pack first.");
            }
            List<Move> moves = [];
            foreach (var item in imports)
            {
                var destination = Path.Combine(Destination(root, item.Kind, profileId), "orion-" + Guid.NewGuid().ToString("N"));
                moves.Add(new(Path.GetRelativePath(work, item.Path), Path.GetRelativePath(root, destination)));
            }
            File.WriteAllText(Path.Combine(work, "moves.tmp"), JsonSerializer.Serialize(moves));
            File.Move(Path.Combine(work, "moves.tmp"), Path.Combine(work, "moves.json"));
            foreach (var move in moves)
            {
                ct.ThrowIfCancellationRequested();
                var dest = ContentFiles.Safe(root, move.Destination);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                Directory.Move(ContentFiles.Safe(work, move.Source), dest);
            }
            File.WriteAllText(Path.Combine(work, "committed"), "ok");
        }
        finally { Recover(root); }
    }, ct);

    private ContentEntry Find(string root, string entryId) => Entries(root, Profiles(root)).SingleOrDefault(e => e.Id == entryId)
        ?? throw new FileNotFoundException("Content no longer exists. Refresh the list.");

    public Task ArchiveAsync(Guid id, string entryId, CancellationToken ct = default) => Task.Run(() =>
    {
        using var lease = activity.Acquire(id); ct.ThrowIfCancellationRequested();
        var root = Root(id); Recover(root);
        var entry = Find(root, entryId);
        if (entry.Archived) throw new InvalidOperationException("Content is already archived.");
        if (entry.Shared) throw new InvalidOperationException("Manage shared packs from the Content library tab. Their source is used by other instances.");
        var trash = ContentFiles.Safe(root, ".content-trash/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(trash);
        File.WriteAllText(Path.Combine(trash, "receipt.tmp"), JsonSerializer.Serialize(new Receipt(entry)));
        File.Move(Path.Combine(trash, "receipt.tmp"), Path.Combine(trash, "receipt.json"));
        // Atomic rename on the instance filesystem; original bytes remain recoverable.
        Directory.Move(ContentFiles.Safe(root, entry.Id), Path.Combine(trash, "content"));
    }, ct);

    public Task RestoreAsync(Guid id, string entryId, CancellationToken ct = default) => Task.Run(() =>
    {
        using var lease = activity.Acquire(id); ct.ThrowIfCancellationRequested();
        var root = Root(id); Recover(root);
        var entry = Find(root, entryId);
        if (!entry.Archived) throw new InvalidOperationException("Content is not archived.");
        var trash = ContentFiles.Safe(root, entry.Id);
        var receipt = ReadReceipt(root, trash);
        Rtx.RtxFamilyPolicy.RequireVanillaImport(root, [entry.PackId]);
        if (entry.Kind == ContentKind.World) Rtx.RtxFamilyPolicy.RequireWorldImport(root, ContentFiles.Safe(trash, "content"));
        if (entry.PackId is not null && Entries(root, Profiles(root)).Any(e => !e.Archived && e.PackId == entry.PackId))
            throw new InvalidOperationException("A pack with this UUID is already installed.");
        var dest = ContentFiles.Safe(root, receipt.Entry.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        Directory.Move(ContentFiles.Safe(trash, "content"), dest);
        ContentFiles.DeleteWork(trash);
    }, ct);

    public Task ExportAsync(Guid id, string entryId, string target, CancellationToken ct = default) => Task.Run(() =>
    {
        using var lease = activity.Acquire(id); ct.ThrowIfCancellationRequested();
        var root = Root(id); Recover(root);
        var entry = Find(root, entryId);
        var source = entry.Shared ? SharedContentLinks.Resolve(paths, Path.Combine(root, entry.Id))
            : ContentFiles.Safe(root, entry.Archived ? Path.Combine(entry.Id, "content") : entry.Id);
        var fullTarget = Path.GetFullPath(target);
        ContentFiles.Safe(Path.GetDirectoryName(fullTarget)!, Path.GetFileName(fullTarget));
        if (fullTarget.StartsWith(root + "/", StringComparison.Ordinal)) throw new InvalidOperationException("Export to a location outside this instance.");
        // CreateNew deliberately refuses overwrites, even after a native picker confirmation.
        using var output = new FileStream(fullTarget, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, true);
            var budget = new ContentFiles.Budget();
            foreach (var file in ContentFiles.Walk(source, ct))
            {
                budget.File();
                using var input = File.OpenRead(file);
                using var item = zip.CreateEntry(Path.GetRelativePath(source, file).Replace('\\', '/'), CompressionLevel.Fastest).Open();
                ContentFiles.Copy(input, item, budget, ct);
            }
        }
        catch { output.Dispose(); File.Delete(fullTarget); throw; }
    }, ct);

    public string GetFolder(Guid id, string entryId)
    {
        var root = Root(id); var entry = Find(root, entryId);
        return entry.Shared ? SharedContentLinks.Resolve(paths, Path.Combine(root, entry.Id))
            : ContentFiles.Safe(root, entry.Archived ? Path.Combine(entry.Id, "content") : entry.Id);
    }
    internal IReadOnlyList<ContentEntry> ListUnderLease(Guid id) { var root = Root(id); Recover(root); InstanceContentDirectories.Ensure(root); return Entries(root, Profiles(root)); }
}

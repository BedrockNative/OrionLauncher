using System.Text.Json;
using Orion.Domain;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Content;

/// <summary>Immutable imported sources; only per-instance user-content leaf links are managed here.</summary>
public sealed class ContentLibraryService(AppPaths paths, InstanceActivity activity, InstanceContentService local)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private sealed record Change(Guid Instance, string Destination, string Source, bool World);
    private string Root => SharedContentLinks.Library(paths);
    private string Item(Guid id) => SharedContentLinks.Item(paths, id);
    private LibraryContent Read(Guid id)
    {
        var item = Item(id);
        var receipt = JsonSerializer.Deserialize<LibraryContent>(ContentFiles.ReadSmall(Path.Combine(item, "item.json")))
            ?? throw new InvalidDataException("Invalid content library metadata.");
        if (receipt.Id != id || receipt.Entries.Count is < 1 or > 1024) throw new InvalidDataException("Invalid library identity.");
        List<ContentEntry> entries = [];
        foreach (var entry in receipt.Entries)
        {
            var expected = entry.Kind == ContentKind.World ? "payload/world" : $"payload/{Guid.Parse(entry.PackId!):N}";
            if (entry.Id != expected) throw new InvalidDataException("Invalid library path.");
            var source = ContentFiles.Safe(item, expected);
            if (entry.Kind == ContentKind.World)
            {
                if (!File.Exists(ContentFiles.Safe(source, "level.dat")) || !Directory.Exists(ContentFiles.Safe(source, "db"))) throw new InvalidDataException("Invalid world template.");
                entries.Add(ContentMetadata.World(item, source, fallbackName: entry.Name));
            }
            else entries.Add(ContentMetadata.Pack(item, source));
        }
        if (entries.Any(e => e.Kind == ContentKind.World) && entries.Count != 1) throw new InvalidDataException("A world cannot be linked as a pack bundle.");
        // Old downloads used the temporary archive name. Repair presentation only;
        // identities, receipts and existing instance links remain untouched.
        return receipt with { Entries = entries, Name = ContentLibraryNames.Resolve(receipt.Name, entries) };
    }
    private async Task<T> Exclusive<T>(Func<T> work, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await Task.Run(() => { Recover(); ct.ThrowIfCancellationRequested(); return work(); }, ct); }
        finally { gate.Release(); }
    }
    public Task<IReadOnlyList<LibraryContent>> ListAsync(CancellationToken ct = default) => Exclusive<IReadOnlyList<LibraryContent>>(() =>
    {
        var items = ContentFiles.Safe(Root, "items");
        if (!Directory.Exists(items)) return [];
        return Directory.EnumerateDirectories(items).Select(Path.GetFileName).Where(n => Guid.TryParseExact(n, "N", out _))
            .Select(n => Read(Guid.Parse(n!))).OrderBy(i => i.Name).ToArray();
    }, ct);

    public Task<LibraryContent> ImportAsync(string file, CancellationToken ct = default, string? projectName = null) => Exclusive(() =>
    {
        var id = Guid.NewGuid(); var work = ContentFiles.Safe(Root, $"staging/{id:N}"); Directory.CreateDirectory(work);
        try
        {
            var parts = ContentArchive.Extract(file, work, ct);
            var staged = ContentFiles.Safe(work, "item"); Directory.CreateDirectory(Path.Combine(staged, "payload"));
            List<ContentEntry> entries = [];
            foreach (var part in parts)
            {
                ct.ThrowIfCancellationRequested();
                var relative = part.Kind == ContentKind.World ? "payload/world" : $"payload/{Guid.Parse(part.Uuid!):N}";
                var destination = Path.Combine(staged, relative); Directory.Move(part.Path, destination);
                if (part.Kind == ContentKind.World)
                {
                    var nameFile = Path.Combine(destination, "levelname.txt");
                    entries.Add(new(relative, File.Exists(nameFile) ? ContentFiles.ReadSmall(nameFile, 4096).Trim() : Path.GetFileNameWithoutExtension(file), part.Kind, "", null, null));
                }
                else entries.Add(ContentMetadata.Pack(staged, destination) with { IconPath = null });
            }
            var name = string.IsNullOrWhiteSpace(projectName)
                ? entries.Count == 1 ? entries[0].Name : Path.GetFileNameWithoutExtension(file)
                : projectName.Trim();
            var item = new LibraryContent(id, ContentLibraryNames.Resolve(name, entries), entries);
            File.WriteAllText(Path.Combine(staged, "item.json"), JsonSerializer.Serialize(item));
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(Root, "items")); Directory.Move(staged, Item(id));
            return Read(id);
        }
        finally { if (Directory.Exists(work)) ContentFiles.DeleteWork(work); }
    }, ct);

    // Entire target set is leased and preflighted before mutation. A durable journal rolls back partial distribution.
    public Task DistributeAsync(Guid itemId, IReadOnlyList<ContentTarget> targets, CancellationToken ct = default) => Exclusive(() =>
    {
        if (targets.Count == 0 || targets.Select(t => t.InstanceId).Distinct().Count() != targets.Count)
            throw new InvalidOperationException("Choose one or more distinct instances.");
        var leases = new List<IDisposable>();
        var transactionId = Guid.NewGuid(); var transaction = ContentFiles.Safe(Root, $"transactions/{transactionId:N}");
        try
        {
            foreach (var target in targets.OrderBy(t => t.InstanceId)) leases.Add(activity.Acquire(target.InstanceId));
            var item = Read(itemId); List<Change> changes = [];
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                var instance = ContentFiles.Safe(paths.Instance(target.InstanceId));
                var existing = local.ListUnderLease(target.InstanceId);
                Rtx.RtxFamilyPolicy.RequireVanillaImport(instance, item.Entries.Select(e => e.PackId));
                foreach (var entry in item.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var source = ContentFiles.Safe(Item(itemId), entry.Id);
                    if (entry.Kind == ContentKind.World) Rtx.RtxFamilyPolicy.RequireWorldImport(instance, source);
                    var parent = InstanceContentService.Destination(instance, entry.Kind, target.ProfileId);
                    var name = entry.Kind == ContentKind.World ? $"orion-library-{transactionId:N}" : SharedContentLinks.Name(itemId, entry.PackId!);
                    var destination = Path.Combine(parent, name);
                    if (new DirectoryInfo(destination).LinkTarget is not null && entry.Kind != ContentKind.World
                        && SharedContentLinks.Resolve(paths, destination) == source) continue;
                    ContentFiles.Safe(parent, name);
                    if (Directory.Exists(destination) || File.Exists(destination) || entry.PackId is not null && existing.Any(e => !e.Archived && e.PackId == entry.PackId))
                        throw new InvalidOperationException("This instance already has a pack with that UUID. Remove/archive the existing pack first.");
                    changes.Add(new(target.InstanceId, Path.GetRelativePath(instance, destination), Path.GetRelativePath(Root, source), entry.Kind == ContentKind.World));
                }
            }
            Directory.CreateDirectory(transaction);
            File.WriteAllText(Path.Combine(transaction, "changes.tmp"), JsonSerializer.Serialize(changes));
            File.Move(Path.Combine(transaction, "changes.tmp"), Path.Combine(transaction, "changes.json"));
            foreach (var change in changes)
            {
                ct.ThrowIfCancellationRequested();
                var destination = Path.Combine(paths.Instance(change.Instance), change.Destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var source = ContentFiles.Safe(Root, change.Source);
                if (change.World) CopyTree(source, destination, new(), ct);
                else Directory.CreateSymbolicLink(destination, source);
            }
            ct.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(transaction, "committed"), "ok");
            return true;
        }
        finally
        {
            try { if (Directory.Exists(transaction)) RecoverTransaction(transaction); }
            finally { foreach (var lease in leases) lease.Dispose(); }
        }
    }, ct);

    public Task UnlinkAsync(Guid itemId, IReadOnlyList<ContentTarget> targets, CancellationToken ct = default) => Exclusive(() =>
    {
        if (targets.Count == 0) throw new InvalidOperationException("Choose one or more instances.");
        var leases = new List<IDisposable>();
        try
        {
            var item = Read(itemId);
            if (item.Entries.Any(e => e.Kind == ContentKind.World)) throw new InvalidOperationException("Manage copied worlds individually in the instance content manager.");
            foreach (var target in targets.DistinctBy(t => t.InstanceId).OrderBy(t => t.InstanceId)) leases.Add(activity.Acquire(target.InstanceId));
            var links = targets.SelectMany(target => item.Entries.Select(entry => Path.Combine(
                InstanceContentService.Destination(ContentFiles.Safe(paths.Instance(target.InstanceId)), entry.Kind, null), SharedContentLinks.Name(itemId, entry.PackId!)))).Distinct().ToArray();
            foreach (var link in links) if (new DirectoryInfo(link).LinkTarget is not null) SharedContentLinks.Resolve(paths, link);
            ct.ThrowIfCancellationRequested();
            // No cancellation midway through this short, prevalidated unlink-only commit.
            foreach (var link in links) if (new DirectoryInfo(link).LinkTarget is not null) Directory.Delete(link);
            return true;
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }, ct);

    private static void CopyTree(string source, string target, ContentFiles.Budget budget, CancellationToken ct, int depth = 0)
    {
        if (depth > 40) throw new InvalidDataException("Content directories are nested too deeply.");
        Directory.CreateDirectory(ContentFiles.Safe(target));
        foreach (var entry in Directory.EnumerateFileSystemEntries(ContentFiles.Safe(source)))
        {
            ct.ThrowIfCancellationRequested(); budget.File(); ContentFiles.Safe(source, Path.GetFileName(entry));
            var destination = ContentFiles.Safe(target, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyTree(entry, destination, budget, ct, depth + 1);
            else { using var input = File.OpenRead(entry); using var output = new FileStream(destination, FileMode.CreateNew); ContentFiles.Copy(input, output, budget, ct); }
        }
    }
    private void Recover()
    {
        var staging = ContentFiles.Safe(Root, "staging");
        if (Directory.Exists(staging)) foreach (var folder in Directory.EnumerateDirectories(staging)) ContentFiles.DeleteWork(ContentFiles.Safe(staging, Path.GetFileName(folder)));
        var transactions = ContentFiles.Safe(Root, "transactions");
        if (!Directory.Exists(transactions)) return;
        foreach (var folder in Directory.EnumerateDirectories(transactions))
        {
            ContentFiles.Safe(transactions, Path.GetFileName(folder));
            var changes = ReadChanges(folder); var leases = new List<IDisposable>();
            try
            {
                foreach (var id in changes.Select(c => c.Instance).Distinct().Order()) leases.Add(activity.Acquire(id));
                RecoverTransaction(folder);
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
        }
    }
    private static Change[] ReadChanges(string transaction) => File.Exists(ContentFiles.Safe(transaction, "changes.json"))
        ? JsonSerializer.Deserialize<Change[]>(ContentFiles.ReadSmall(Path.Combine(transaction, "changes.json"))) ?? [] : [];
    private void RecoverTransaction(string transaction)
    {
        var tx = Guid.ParseExact(Path.GetFileName(transaction), "N");
        if (!File.Exists(ContentFiles.Safe(transaction, "committed")))
            foreach (var change in ReadChanges(transaction).Reverse())
            {
                var root = ContentFiles.Safe(paths.Instance(change.Instance));
                InstanceContentService.ValidateDestination(root, change.Destination, allowManagedLeaf: true);
                var destination = Path.Combine(root, change.Destination);
                if (change.World)
                {
                    if (Path.GetFileName(destination) != $"orion-library-{tx:N}" || Path.GetFileName(Path.GetDirectoryName(destination)) != "minecraftWorlds")
                        throw new InvalidDataException("Invalid world distribution journal.");
                    if (Directory.Exists(ContentFiles.Safe(root, change.Destination))) ContentFiles.DeleteWork(destination);
                }
                else if (new DirectoryInfo(destination).LinkTarget is not null)
                {
                    if (SharedContentLinks.Resolve(paths, destination) != ContentFiles.Safe(Root, change.Source)) throw new InvalidDataException("Invalid link rollback target.");
                    Directory.Delete(destination);
                }
            }
        ContentFiles.DeleteWork(transaction);
    }
    internal static void RequireRecovered(AppPaths paths, Guid instance)
    {
        var transactions = ContentFiles.Safe(SharedContentLinks.Library(paths), "transactions");
        if (!Directory.Exists(transactions)) return;
        foreach (var transaction in Directory.EnumerateDirectories(transactions))
            if (ReadChanges(ContentFiles.Safe(transactions, Path.GetFileName(transaction))).Any(c => c.Instance == instance))
                throw new InvalidOperationException("Open Content and refresh to recover an interrupted content distribution before playing.");
    }
}

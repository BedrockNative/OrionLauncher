using System.Text.Json;
using Orion.Domain;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.Games;

namespace Orion.Tests;

public sealed class SharedContentTests
{
    [Fact]
    public async Task DownloadBundlesKeepProjectNamesAndLegacyReceiptsRecoverManifestNamesWithoutMutation()
    {
        using var dir = new TestDirectory(); var activity = new InstanceActivity();
        var local = new InstanceContentService(dir.Paths, activity);
        var library = new ContentLibraryService(dir.Paths, activity, local);
        var archive = ContentTests.Zip(dir, ".mcaddon",
            ("bp/manifest.json", ContentTests.Manifest("Verity (4.1.3) BP", "data")),
            ("rp/manifest.json", ContentTests.Manifest("Verity (4.1.3) RP", "resources")));
        var download = Path.Combine(dir.Root, "download.mcaddon"); File.Move(archive, download);
        var item = await library.ImportAsync(download);
        Assert.Equal("Verity (4.1.3)", item.Name);
        var receipt = Path.Combine(dir.Paths.Data, "content-library/items", item.Id.ToString("N"), "item.json");
        var legacy = JsonSerializer.Serialize(item with { Name = "download" }); File.WriteAllText(receipt, legacy);
        var restored = Assert.Single(await library.ListAsync());
        Assert.Equal("Verity (4.1.3)", restored.Name); Assert.Equal(item.Id, restored.Id);
        Assert.Equal(item.Entries.Select(e => e.Id), restored.Entries.Select(e => e.Id));
        Assert.Equal(legacy, File.ReadAllText(receipt));
        var project = await library.ImportAsync(download, projectName: "Verity — Bedrock Edition");
        Assert.Equal("Verity — Bedrock Edition", project.Name);
        Assert.Equal(project.Name, (await library.ListAsync()).Single(i => i.Id == project.Id).Name);
    }

    [Fact]
    public async Task MeaningfulArchiveNamesArePreservedAndUnrelatedPacksAreNotMergedByPrefix()
    {
        using var dir = new TestDirectory(); var activity = new InstanceActivity();
        var library = new ContentLibraryService(dir.Paths, activity, new(dir.Paths, activity));
        var archive = ContentTests.Zip(dir, ".mcaddon", ("bp/manifest.json", ContentTests.Manifest("Forest animals BP", "data")),
            ("rp/manifest.json", ContentTests.Manifest("Forest blocks RP", "resources")));
        var named = Path.Combine(dir.Root, "My collection.mcaddon"); File.Move(archive, named);
        Assert.Equal("My collection", (await library.ImportAsync(named)).Name);
        var generic = Path.Combine(dir.Root, "download.mcaddon"); File.Move(named, generic);
        Assert.Equal("Forest animals BP", (await library.ImportAsync(generic)).Name);
    }

    [Fact]
    public async Task BundleUsesOneSourceAcrossInstancesAndNeverTouchesVanilla()
    {
        using var dir = new TestDirectory(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        ContentTests.Setup(dir, a); ContentTests.Setup(dir, b);
        var activity = new InstanceActivity(); var local = new InstanceContentService(dir.Paths, activity); var library = new ContentLibraryService(dir.Paths, activity, local);
        var vanilla = Path.Combine(dir.Paths.Game(a), "data/behavior_packs/vanilla"); Directory.CreateDirectory(vanilla); File.WriteAllText(Path.Combine(vanilla, "keep"), "vanilla");
        var pack = ContentTests.Zip(dir, ".mcaddon", ("bp/manifest.json", ContentTests.Manifest("Behavior", "data")),
            ("rp/manifest.json", ContentTests.Manifest("Texture", "resources")), ("bp/user.txt", "shared bytes"));
        var item = await library.ImportAsync(pack);
        await library.DistributeAsync(item.Id, [new(a, null), new(b, null)]);
        var first = (await local.ListAsync(a)).Entries; var second = (await local.ListAsync(b)).Entries;
        Assert.Equal(2, first.Count); Assert.All(first, e => Assert.True(e.Shared));
        foreach (var entry in first)
        {
            var targetA = new DirectoryInfo(Path.Combine(dir.Paths.Instance(a), entry.Id)).LinkTarget;
            var targetB = new DirectoryInfo(Path.Combine(dir.Paths.Instance(b), second.Single(e => e.PackId == entry.PackId).Id)).LinkTarget;
            Assert.Equal(targetA, targetB); Assert.StartsWith(Path.Combine(dir.Paths.Data, "content-library/items"), targetA);
            Assert.Contains("Shared/games/com.mojang/", entry.Id);
        }
        await library.DistributeAsync(item.Id, [new(a, null)]); // idempotent, not another copy
        Assert.Equal(2, (await local.ListAsync(a)).Entries.Count);
        Assert.Equal("vanilla", File.ReadAllText(Path.Combine(vanilla, "keep")));
        await library.UnlinkAsync(item.Id, [new(a, null)]);
        Assert.Empty((await local.ListAsync(a)).Entries); Assert.Equal(2, (await local.ListAsync(b)).Entries.Count);
        Assert.Single(await library.ListAsync()); Assert.True(File.Exists(pack));
        var export = Path.Combine(dir.Root, "shared.mcpack");
        await local.ExportAsync(b, second[0].Id, export); Assert.True(File.Exists(export));
        await Assert.ThrowsAsync<InvalidOperationException>(() => local.ArchiveAsync(b, second[0].Id));
    }
    [Fact]
    public async Task WorldsAreIndependentCopiesAndNeedExplicitProfiles()
    {
        using var dir = new TestDirectory(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        ContentTests.Setup(dir, a); ContentTests.Setup(dir, b);
        var activity = new InstanceActivity(); var local = new InstanceContentService(dir.Paths, activity); var library = new ContentLibraryService(dir.Paths, activity, local);
        var world = ContentTests.Zip(dir, ".mcworld", ("level.dat", "world"), ("db/0001.ldb", "original"));
        var item = await library.ImportAsync(world);
        await Assert.ThrowsAsync<InvalidOperationException>(() => library.DistributeAsync(item.Id, [new(a, null)]));
        var ap = (await local.ListAsync(a)).Profiles.Single().Id; var bp = (await local.ListAsync(b)).Profiles.Single().Id;
        await library.DistributeAsync(item.Id, [new(a, ap), new(b, bp)]);
        var ae = (await local.ListAsync(a)).Entries.Single(); var be = (await local.ListAsync(b)).Entries.Single();
        var af = local.GetFolder(a, ae.Id); var bf = local.GetFolder(b, be.Id);
        Assert.Null(new DirectoryInfo(af).LinkTarget); Assert.Null(new DirectoryInfo(bf).LinkTarget);
        File.WriteAllText(Path.Combine(af, "db/0001.ldb"), "changed by player A");
        Assert.Equal("original", File.ReadAllText(Path.Combine(bf, "db/0001.ldb")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => library.UnlinkAsync(item.Id, [new(a, ap)]));
    }
    [Fact]
    public async Task DuplicateUuidAndRunningTargetsFailBeforeAnyLinksAreCreated()
    {
        using var dir = new TestDirectory(); var a = Guid.NewGuid(); var b = Guid.NewGuid(); ContentTests.Setup(dir, a); ContentTests.Setup(dir, b);
        var activity = new InstanceActivity(); var local = new InstanceContentService(dir.Paths, activity); var library = new ContentLibraryService(dir.Paths, activity, local);
        var file = ContentTests.Zip(dir, ".mcpack", ("manifest.json", ContentTests.Manifest("Pack", "data")));
        var item = await library.ImportAsync(file);
        using (activity.Acquire(b)) await Assert.ThrowsAsync<InvalidOperationException>(() => library.DistributeAsync(item.Id, [new(a, null), new(b, null)]));
        Assert.Empty((await local.ListAsync(a)).Entries);
        await local.ImportAsync(b, file, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => library.DistributeAsync(item.Id, [new(a, null), new(b, null)]));
        Assert.Empty((await local.ListAsync(a)).Entries); Assert.Single((await local.ListAsync(b)).Entries);
    }
    [Fact]
    public async Task ManifestMetadataAndLanguageTokensAreReadWithoutChangingThePack()
    {
        using var dir = new TestDirectory(); var activity = new InstanceActivity(); var local = new InstanceContentService(dir.Paths, activity); var library = new ContentLibraryService(dir.Paths, activity, local);
        var manifest = JsonSerializer.Serialize(new { format_version = 2, header = new { name = "pack.name", description = "pack.description", uuid = Guid.NewGuid(), version = new[] { 2, 3, 4 }, min_engine_version = new[] { 1, 21, 90 } }, modules = new[] { new { type = "resources" } } });
        var file = ContentTests.Zip(dir, ".mcpack", ("manifest.json", manifest), ("texts/en_US.lang", "pack.name=My texture\npack.description=Custom blocks ## comment"), ("pack_icon.png", "placeholder"));
        var entry = (await library.ImportAsync(file)).Entries.Single();
        Assert.Equal("My texture", entry.Name); Assert.Equal("Custom blocks", entry.Description);
        Assert.Equal("2.3.4", entry.Version); Assert.Equal("1.21.90", entry.MinimumEngineVersion); Assert.NotNull(entry.IconPath);
    }
    [Fact]
    public async Task ForgedLinksAreNeverFollowedAndWorldFoldersNeverAcceptPackLinks()
    {
        using var dir = new TestDirectory(); var id = Guid.NewGuid(); var users = ContentTests.Setup(dir, id);
        var activity = new InstanceActivity(); var local = new InstanceContentService(dir.Paths, activity); var library = new ContentLibraryService(dir.Paths, activity, local);
        var item = await library.ImportAsync(ContentTests.Zip(dir, ".mcpack", ("manifest.json", ContentTests.Manifest("Pack", "data"))));
        await library.DistributeAsync(item.Id, [new(id, null)]);
        var entry = (await local.ListAsync(id)).Entries.Single(); var link = Path.Combine(dir.Paths.Instance(id), entry.Id);
        Directory.Delete(link); Directory.CreateSymbolicLink(link, dir.Root);
        await Assert.ThrowsAsync<InvalidDataException>(() => local.ListAsync(id));
        await Assert.ThrowsAsync<InvalidDataException>(() => library.UnlinkAsync(item.Id, [new(id, null)]));
        Assert.True(Directory.Exists(dir.Root)); Directory.Delete(link);
        var worlds = Path.Combine(users, "12345/games/com.mojang/minecraftWorlds"); Directory.CreateDirectory(worlds);
        Directory.CreateSymbolicLink(Path.Combine(worlds, Path.GetFileName(link)), Path.Combine(dir.Paths.Data, "content-library/items", item.Id.ToString("N"), item.Entries[0].Id));
        await Assert.ThrowsAsync<InvalidDataException>(() => local.ListAsync(id));
    }
    [Fact]
    public async Task CrashRecoveryRollsBackOwnedLinksAndPartialWorldCopiesBeforePlay()
    {
        using var dir = new TestDirectory(); var id = Guid.NewGuid(); ContentTests.Setup(dir, id);
        var activity = new InstanceActivity(); var local = new InstanceContentService(dir.Paths, activity); var library = new ContentLibraryService(dir.Paths, activity, local);
        var item = await library.ImportAsync(ContentTests.Zip(dir, ".mcpack", ("manifest.json", ContentTests.Manifest("Pack", "data"))));
        await library.DistributeAsync(item.Id, [new(id, null)]);
        var entry = (await local.ListAsync(id)).Entries.Single();
        var libraryRoot = Path.Combine(dir.Paths.Data, "content-library"); var tx = Guid.NewGuid();
        var transaction = Path.Combine(libraryRoot, "transactions", tx.ToString("N")); Directory.CreateDirectory(transaction);
        var profile = (await local.ListAsync(id)).Profiles.Single().Id;
        var worldRelative = Path.Combine(profile, "minecraftWorlds", $"orion-library-{tx:N}");
        var worldPath = Path.Combine(dir.Paths.Instance(id), worldRelative); Directory.CreateDirectory(worldPath); File.WriteAllText(Path.Combine(worldPath, "partial"), "partial copy");
        File.WriteAllText(Path.Combine(transaction, "changes.json"), JsonSerializer.Serialize(new[]
        {
            new { Instance = id, Destination = entry.Id, Source = $"items/{item.Id:N}/{item.Entries[0].Id}", World = false },
            new { Instance = id, Destination = worldRelative, Source = "unused", World = true }
        }));
        Assert.Throws<InvalidOperationException>(() => local.Recover(id));
        await library.ListAsync();
        Assert.Empty((await local.ListAsync(id)).Entries); Assert.False(Directory.Exists(transaction)); Assert.False(Directory.Exists(worldPath));
        Assert.Single(await library.ListAsync()); local.Recover(id);
    }
    [Fact]
    public async Task CancelledAndUnsafeImportsDoNotPublishEntries()
    {
        using var dir = new TestDirectory(); var activity = new InstanceActivity(); var local = new InstanceContentService(dir.Paths, activity); var library = new ContentLibraryService(dir.Paths, activity, local);
        var unsafeFile = ContentTests.Zip(dir, ".mcaddon", ("../escape", "no"));
        await Assert.ThrowsAsync<InvalidDataException>(() => library.ImportAsync(unsafeFile)); Assert.Empty(await library.ListAsync());
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.ImportAsync(unsafeFile, ct.Token));
        Assert.Empty(await library.ListAsync());
    }
}

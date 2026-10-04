using System.IO.Compression;
using System.Text.Json;
using Orion.Domain;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.Games;

namespace Orion.Tests;

public sealed class ContentTests
{
    internal static string Setup(TestDirectory dir, Guid id, string player = "12345")
    {
        var users = Path.Combine(dir.Paths.Prefix(id), "drive_c/users/player/AppData/Roaming/Minecraft Bedrock/Users");
        Directory.CreateDirectory(Path.Combine(users, "Shared/games/com.mojang"));
        Directory.CreateDirectory(Path.Combine(users, player, "games/com.mojang"));
        return users;
    }
    internal static string Manifest(string name, string type, string? uuid = null) => JsonSerializer.Serialize(new
    {
        format_version = 2, header = new { name, uuid = uuid ?? Guid.NewGuid().ToString(), version = new[] { 1, 0, 0 } },
        modules = new[] { new { type, uuid = Guid.NewGuid().ToString(), version = new[] { 1, 0, 0 } } }
    });
    internal static string Zip(TestDirectory dir, string extension, params (string Name, string Data)[] files)
    {
        var path = Path.Combine(dir.Root, Guid.NewGuid() + extension);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in files) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(data); }
        return path;
    }
    [Fact]
    public async Task WorldImportExportArchiveRestorePreservesBytesAndSeparatesProfiles()
    {
        using var dir = new TestDirectory(); var id = Guid.NewGuid(); Setup(dir, id); Setup(dir, id, "67890");
        var service = new InstanceContentService(dir.Paths, new());
        var profiles = (await service.ListAsync(id)).Profiles;
        var world = Zip(dir, ".mcworld", ("level.dat", "world metadata"), ("levelname.txt", "My world"), ("db/000001.ldb", "world bytes"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(id, world, null));
        await service.ImportAsync(id, world, profiles[0].Id);
        var entry = Assert.Single((await service.ListAsync(id)).Entries);
        Assert.Equal(profiles[0].Id, entry.ProfileId); Assert.Equal("My world", entry.Name);
        var output = Path.Combine(dir.Root, "export.mcworld");
        await service.ExportAsync(id, entry.Id, output);
        using (var zip = ZipFile.OpenRead(output))
        using (var reader = new StreamReader(zip.GetEntry("db/000001.ldb")!.Open())) Assert.Equal("world bytes", reader.ReadToEnd());
        await Assert.ThrowsAsync<IOException>(() => service.ExportAsync(id, entry.Id, output));
        await service.ArchiveAsync(id, entry.Id);
        var archived = Assert.Single((await service.ListAsync(id)).Entries); Assert.True(archived.Archived);
        Assert.False(Directory.Exists(Path.Combine(dir.Paths.Instance(id), entry.Id)));
        await service.RestoreAsync(id, archived.Id);
        var restored = Assert.Single((await service.ListAsync(id)).Entries); Assert.Equal(entry, restored);
        Assert.Equal("world bytes", await File.ReadAllTextAsync(Path.Combine(dir.Paths.Instance(id), restored.Id, "db/000001.ldb")));
    }
    [Fact]
    public async Task BundleImportsBothKindsAndRejectsDuplicateUuidsWithoutPartialInstall()
    {
        using var dir = new TestDirectory(); var id = Guid.NewGuid(); Setup(dir, id);
        var service = new InstanceContentService(dir.Paths, new()); var uuid = Guid.NewGuid().ToString();
        var addon = Zip(dir, ".mcaddon", ("behavior/manifest.json", Manifest("Addon", "data", uuid)), ("resource/manifest.json", Manifest("Textures", "resources")));
        await service.ImportAsync(id, addon, null);
        var entries = (await service.ListAsync(id)).Entries; Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Kind == ContentKind.Addon); Assert.Contains(entries, e => e.Kind == ContentKind.Texture);
        var duplicate = Zip(dir, ".mcaddon", ("new/manifest.json", Manifest("Other", "data")), ("old/manifest.json", Manifest("Duplicate", "data", uuid)));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(id, duplicate, null));
        Assert.Equal(2, (await service.ListAsync(id)).Entries.Count);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(dir.Paths.Instance(id), ".content-work")));
    }
    [Fact]
    public async Task NestedMcpackBundleIsSupported()
    {
        using var dir = new TestDirectory(); var id = Guid.NewGuid(); Setup(dir, id);
        var pack = Zip(dir, ".mcpack", ("manifest.json", Manifest("Nested", "script")));
        var bundle = Path.Combine(dir.Root, "nested.mcaddon");
        using (var archive = ZipFile.Open(bundle, ZipArchiveMode.Create)) archive.CreateEntryFromFile(pack, "nested.mcpack");
        var service = new InstanceContentService(dir.Paths, new()); await service.ImportAsync(id, bundle, null);
        Assert.Equal("Nested", Assert.Single((await service.ListAsync(id)).Entries).Name);
    }
    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("folder/../../escape")]
    [InlineData("..\\escape")]
    [InlineData("folder\\..\\..\\escape")]
    [InlineData("C:/escape")]
    [InlineData("a/CON.txt")]
    [InlineData("a/name. ")]
    public async Task UnsafeArchivePathsAreRejectedAndStagingIsCleaned(string name)
    {
        using var dir = new TestDirectory(); var id = Guid.NewGuid(); Setup(dir, id);
        var service = new InstanceContentService(dir.Paths, new());
        var file = Zip(dir, ".mcpack", ("manifest.json", Manifest("Unsafe", "data")), (name, "bad"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(id, file, null));
        Assert.Empty((await service.ListAsync(id)).Entries);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(dir.Paths.Instance(id), ".content-work")));
    }
    [Fact]
    public async Task CaseCollisionsAndArchiveSymlinksAreRejected()
    {
        using var dir = new TestDirectory(); var id = Guid.NewGuid(); Setup(dir, id);
        var service = new InstanceContentService(dir.Paths, new());
        var file = Zip(dir, ".mcpack", ("manifest.json", Manifest("Unsafe", "data")), ("Folder/a", "a"), ("folder/b", "b"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(id, file, null));
        var link = Zip(dir, ".mcpack", ("manifest.json", Manifest("Unsafe", "data")), ("link", "/etc"));
        using (var zip = ZipFile.Open(link, ZipArchiveMode.Update)) zip.GetEntry("link")!.ExternalAttributes = unchecked((int)0xa1ff0000);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(id, link, null));
    }
    [Fact]
    public async Task WindowsSeparatorsAndRelativeRootAreNormalizedSafely()
    {
        using var dir = new TestDirectory(); var id = Guid.NewGuid(); Setup(dir, id);
        var service = new InstanceContentService(dir.Paths, new());
        var file = Zip(dir, ".mcpack", ("./", ""), ("./Pack\\manifest.json", Manifest("Windows pack", "script")), ("Pack\\scripts\\main.js", "// fixture"));
        await service.ImportAsync(id, file, null);
        var entry = Assert.Single((await service.ListAsync(id)).Entries);
        Assert.True(File.Exists(Path.Combine(dir.Paths.Instance(id), entry.Id, "scripts/main.js")));
        var collision = Zip(dir, ".mcpack", ("manifest.json", Manifest("Collision", "data")), ("a\\b", "one"), ("a/b", "two"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(id, collision, null));
    }
    [Fact]
    public async Task SharedGameLeaseAndCancellationPreventContentChanges()
    {
        using var dir = new TestDirectory(); var id = Guid.NewGuid(); Setup(dir, id);
        var activity = new InstanceActivity(); var service = new InstanceContentService(dir.Paths, activity);
        var file = Zip(dir, ".mcpack", ("manifest.json", Manifest("Pack", "resources")));
        using (activity.Acquire(id))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(id, file, null));
            Assert.Throws<InvalidOperationException>(() => activity.Acquire(id));
        }
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(id, file, null, ct.Token));
        Assert.Empty((await service.ListAsync(id)).Entries);
        await service.ImportAsync(id, file, null);
        Assert.Single((await service.ListAsync(id)).Entries);
    }
    [Fact]
    public async Task RefusesLinkedContentRootsAndNeverReadsOutsideInstance()
    {
        using var dir = new TestDirectory(); var id = Guid.NewGuid(); var users = Setup(dir, id);
        var outside = Path.Combine(dir.Root, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(users, "Shared/games/com.mojang/resource_packs"), outside);
        var service = new InstanceContentService(dir.Paths, new());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(id)); Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }
    [Fact]
    public async Task InterruptedTransactionRollsBackBeforeNextOperation()
    {
        using var dir = new TestDirectory(); var id = Guid.NewGuid(); Setup(dir, id);
        var service = new InstanceContentService(dir.Paths, new());
        await service.ImportAsync(id, Zip(dir, ".mcpack", ("manifest.json", Manifest("Uncommitted", "data"))), null);
        var entry = Assert.Single((await service.ListAsync(id)).Entries);
        var work = Path.Combine(dir.Paths.Instance(id), ".content-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        await File.WriteAllTextAsync(Path.Combine(work, "moves.json"), JsonSerializer.Serialize(new[] { new { Source = "extracted/pack", Destination = entry.Id } }));
        Assert.Empty((await service.ListAsync(id)).Entries);
        Assert.False(Directory.Exists(work));
    }
    [Fact]
    public async Task RestoreDoesNotOverwriteNewerInstalledPack()
    {
        using var dir = new TestDirectory(); var id = Guid.NewGuid(); Setup(dir, id);
        var service = new InstanceContentService(dir.Paths, new());
        var file = Zip(dir, ".mcpack", ("manifest.json", Manifest("Pack", "data")));
        await service.ImportAsync(id, file, null); var entry = Assert.Single((await service.ListAsync(id)).Entries);
        await service.ArchiveAsync(id, entry.Id); var archived = Assert.Single((await service.ListAsync(id)).Entries);
        await service.ImportAsync(id, file, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(id, archived.Id));
        Assert.Equal(2, (await service.ListAsync(id)).Entries.Count);
    }
}

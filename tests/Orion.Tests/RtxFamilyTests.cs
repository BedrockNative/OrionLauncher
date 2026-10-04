using System.Text.Json;
using Orion.Domain;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Rtx;

namespace Orion.Tests;

public sealed class RtxFamilyTests
{
    [Fact]
    public async Task EmbeddedVanillaWorldResourcesCannotBypassProviderSeparation()
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir); var activity = new InstanceActivity();
        ContentTests.Setup(dir, instance.Id);
        var content = new InstanceContentService(dir.Paths, activity); var service = new RtxService(dir.Paths, activity, new RtxTests.Catalog(), content);
        var profile = (await content.ListAsync(instance.Id)).Profiles[0].Id;
        var world = ContentTests.Zip(dir, ".mcworld", ("level.dat", "fixture"), ("levelname.txt", "RTX world"), ("db/000001.ldb", "world bytes"),
            ("resource_packs/renamed/manifest.json", ContentTests.Manifest("Renamed", "resources", RtxTests.Opus().PackId)));
        await service.InstallAsync(instance, RtxTests.Preset(), true, null, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => content.ImportAsync(instance.Id, world, profile));
        var library = new ContentLibraryService(dir.Paths, activity, content); var item = await library.ImportAsync(world);
        await Assert.ThrowsAsync<InvalidOperationException>(() => library.DistributeAsync(item.Id, [new(instance.Id, profile)]));
        await service.RestoreAsync(instance.Id);
        await content.ImportAsync(instance.Id, world, profile);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(instance, RtxTests.Preset(), true, null, default));
    }

    private static string Pack(TestDirectory dir) => ContentTests.Zip(dir, ".mcpack",
        ("manifest.json", ContentTests.Manifest("Renamed official pack", "resources", RtxTests.Opus().PackId)));

    [Fact]
    public async Task BetterBlocksDirectBundleAndSharedVanillaImportsButNotUnrelatedTextures()
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir); var activity = new InstanceActivity();
        var content = new InstanceContentService(dir.Paths, activity); var service = new RtxService(dir.Paths, activity, new RtxTests.Catalog(), content);
        await service.InstallAsync(instance, RtxTests.Preset(), true, null, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => content.ImportAsync(instance.Id, Pack(dir), null));
        var bundle = ContentTests.Zip(dir, ".mcaddon", ("a/manifest.json", ContentTests.Manifest("Other", "data")),
            ("b/manifest.json", ContentTests.Manifest("Renamed", "resources", RtxTests.Opus().PackId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => content.ImportAsync(instance.Id, bundle, null));
        Assert.Empty((await content.ListAsync(instance.Id)).Entries);
        var library = new ContentLibraryService(dir.Paths, activity, content); var item = await library.ImportAsync(Pack(dir));
        await Assert.ThrowsAsync<InvalidOperationException>(() => library.DistributeAsync(item.Id, [new(instance.Id, null)]));
        await content.ImportAsync(instance.Id, ContentTests.Zip(dir, ".mcpack", ("manifest.json", ContentTests.Manifest("Unrelated PBR", "resources"))), null);
        Assert.Single((await content.ListAsync(instance.Id)).Entries);
    }

    [Fact]
    public async Task VanillaBlocksShaderInstallAndConfigurationUntilArchivedAndPreferencesReset()
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir); var activity = new InstanceActivity();
        var content = new InstanceContentService(dir.Paths, activity); var service = new RtxService(dir.Paths, activity, new RtxTests.Catalog(), content);
        await content.ImportAsync(instance.Id, Pack(dir), null);
        Assert.NotNull((await service.InspectAsync(instance.Id)).FamilyConflict);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(instance, RtxTests.Preset(), true, null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfigureAsync(instance.Id, new(true)));
        await service.ConfigureAsync(instance.Id, new(true), family: RtxFamily.VanillaRtx);
        var entry = Assert.Single((await content.ListAsync(instance.Id)).Entries);
        await content.ArchiveAsync(instance.Id, entry.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(instance, RtxTests.Preset(), true, null, default));
        await service.ConfigureAsync(instance.Id, new(), family: RtxFamily.VanillaRtx);
        await service.InstallAsync(instance, RtxTests.Preset(), true, null, default);
        var archived = Assert.Single((await content.ListAsync(instance.Id)).Entries);
        await Assert.ThrowsAsync<InvalidOperationException>(() => content.RestoreAsync(instance.Id, archived.Id));
        Assert.True(Assert.Single((await content.ListAsync(instance.Id)).Entries).Archived);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacySettingsStayWithBetterAfterRestoreAndNeverLeakToVanilla(bool corruptReceipt)
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir); var activity = new InstanceActivity();
        var content = new InstanceContentService(dir.Paths, activity); var service = new RtxService(dir.Paths, activity, new RtxTests.Catalog(), content);
        await service.InstallAsync(instance, RtxTests.Preset(), true, null, default);
        var root = dir.Paths.Instance(instance.Id);
        File.Delete(Path.Combine(root, "rtx/betterrtx/configuration.json"));
        File.Delete(Path.Combine(root, "rtx/vanillartx/configuration.json"));
        await File.WriteAllTextAsync(Path.Combine(root, "rtx/configuration.json"), JsonSerializer.Serialize(new RtxConfiguration(true, true, true)));
        if (corruptReceipt) await File.WriteAllTextAsync(Path.Combine(root, "rtx/current.json"), "broken JSON");
        await service.RestoreAsync(instance.Id);
        Assert.True((await service.InspectAsync(instance.Id)).Configuration.EnableOnLaunch);
        Assert.False((await service.InspectAsync(instance.Id, family: RtxFamily.VanillaRtx)).Configuration.EnableOnLaunch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => content.ImportAsync(instance.Id, Pack(dir), null));
        await service.ConfigureAsync(instance.Id, new());
        await content.ImportAsync(instance.Id, Pack(dir), null);
        await service.ConfigureAsync(instance.Id, new(true), family: RtxFamily.VanillaRtx);
        Assert.False((await service.InspectAsync(instance.Id)).Configuration.EnableOnLaunch);
        Assert.True((await service.InspectAsync(instance.Id, family: RtxFamily.VanillaRtx)).Configuration.EnableOnLaunch);
    }

    [Fact]
    public async Task ExistingMixedInstanceIsBlockedWithoutRemovingItsResourcesAndCanRecover()
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir); var activity = new InstanceActivity();
        var content = new InstanceContentService(dir.Paths, activity); var service = new RtxService(dir.Paths, activity, new RtxTests.Catalog(), content);
        await service.InstallAsync(instance, RtxTests.Preset(), true, null, default);
        var users = ContentTests.Setup(dir, instance.Id);
        var packDir = Path.Combine(users, "Shared/games/com.mojang/resource_packs/manual"); Directory.CreateDirectory(packDir);
        var manifest = Path.Combine(packDir, "manifest.json");
        await File.WriteAllTextAsync(manifest, ContentTests.Manifest("Opus", "resources", RtxTests.Opus().PackId));
        Assert.NotNull((await service.InspectAsync(instance.Id)).FamilyConflict);
        Assert.Throws<InvalidOperationException>(() => service.ValidateForLaunchUnderLease(instance.Id));
        Assert.True(File.Exists(manifest)); Assert.NotNull((await service.InspectAsync(instance.Id)).Installation);
        await service.RestoreAsync(instance.Id);
        Assert.True(File.Exists(manifest)); service.ValidateForLaunchUnderLease(instance.Id);
    }

    [Fact]
    public async Task MissingReceiptRedirectsStillBlockVanillaImports()
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir); var activity = new InstanceActivity();
        var content = new InstanceContentService(dir.Paths, activity); var service = new RtxService(dir.Paths, activity, new RtxTests.Catalog(), content);
        await service.InstallAsync(instance, RtxTests.Preset(), true, null, default);
        File.Delete(Path.Combine(dir.Paths.Instance(instance.Id), "rtx/current.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => content.ImportAsync(instance.Id, Pack(dir), null));
        Assert.Empty((await content.ListAsync(instance.Id)).Entries);
    }
}

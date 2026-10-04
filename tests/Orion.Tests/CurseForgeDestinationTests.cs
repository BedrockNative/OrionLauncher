using Orion.Desktop.I18n;
using Orion.Desktop.ViewModels;
using Orion.Domain;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.Games;

namespace Orion.Tests;

public sealed class CurseForgeDestinationTests
{
    [Fact]
    public async Task SharedLibraryDoesNotDistributeAndSingleInstanceInstallationRemainsLocal()
    {
        using var dir = new TestDirectory(); var a = GameInstance.Create("First", "26", "Release"); var b = GameInstance.Create("Second", "26", "Release");
        foreach (var instance in new[] { a, b }) ContentTests.Setup(dir, instance.Id);
        var activity = new InstanceActivity(); var local = new InstanceContentService(dir.Paths, activity);
        var library = new ContentLibraryService(dir.Paths, activity, local);
        var destinations = new CurseForgeDestinationsViewModel(library, local, () => Task.FromResult<IReadOnlyList<GameInstance>>([a, b]), new Localizer());
        await destinations.RefreshAsync(default);
        var captured = destinations.Capture(false); Assert.Empty(captured.Targets);
        await destinations.InstallAsync(ContentTests.Zip(dir, ".mcpack", ("manifest.json", ContentTests.Manifest("Shared", "resources"))), captured, default, "CurseForge project title");
        Assert.Equal("CurseForge project title", Assert.Single(await library.ListAsync()).Name);
        foreach (var instance in new[] { a, b })
            Assert.Empty((await local.ListAsync(instance.Id)).Entries);
        destinations.ModeIndex = 1; destinations.Target = destinations.Targets[0];
        await destinations.InstallAsync(ContentTests.Zip(dir, ".mcaddon", ("manifest.json", ContentTests.Manifest("Local", "data"))), destinations.Capture(false), default);
        Assert.False((await local.ListAsync(a.Id)).Entries.Single(e => e.Name == "Local").Shared);
        Assert.Empty((await local.ListAsync(b.Id)).Entries); Assert.Single(await library.ListAsync());
    }

    [Fact]
    public async Task WorldTargetsRequireStorageProfilesAndReceiveIndependentCopies()
    {
        using var dir = new TestDirectory(); var a = GameInstance.Create("First", "26", "Release"); var b = GameInstance.Create("Second", "26", "Release");
        foreach (var instance in new[] { a, b }) ContentTests.Setup(dir, instance.Id);
        var activity = new InstanceActivity(); var local = new InstanceContentService(dir.Paths, activity);
        var library = new ContentLibraryService(dir.Paths, activity, local);
        var destinations = new CurseForgeDestinationsViewModel(library, local, () => Task.FromResult<IReadOnlyList<GameInstance>>([a, b]), new Localizer());
        await destinations.RefreshAsync(default); destinations.ModeIndex = 1; destinations.Target = destinations.Targets[1];
        var profile = destinations.Targets[1].Profile; destinations.Targets[1].Profile = null;
        Assert.Throws<InvalidOperationException>(() => destinations.Capture(true));
        destinations.Targets[1].Profile = profile;
        var archive = ContentTests.Zip(dir, ".mcworld", ("level.dat", "metadata"), ("levelname.txt", "Adventure"), ("db/test.ldb", "save"));
        await destinations.InstallAsync(archive, destinations.Capture(true), default);
        Assert.Empty((await local.ListAsync(a.Id)).Entries);
        destinations.Target = destinations.Targets[0];
        await destinations.InstallAsync(archive, destinations.Capture(true), default);
        var first = Assert.Single((await local.ListAsync(a.Id)).Entries);
        var second = Assert.Single((await local.ListAsync(b.Id)).Entries);
        Assert.False(first.Shared); Assert.False(second.Shared);
        Assert.Null(new DirectoryInfo(Path.Combine(dir.Paths.Instance(a.Id), first.Id)).LinkTarget);
        File.WriteAllText(Path.Combine(dir.Paths.Instance(a.Id), first.Id, "levelname.txt"), "Edited");
        Assert.Equal("Adventure", Assert.Single((await local.ListAsync(b.Id)).Entries).Name);
    }

    [Fact]
    public async Task LibraryWorksWithoutInstancesAndRejectsBulkDestinations()
    {
        using var dir = new TestDirectory(); var activity = new InstanceActivity();
        var local = new InstanceContentService(dir.Paths, activity); var library = new ContentLibraryService(dir.Paths, activity, local);
        var destinations = new CurseForgeDestinationsViewModel(library, local, () => Task.FromResult<IReadOnlyList<GameInstance>>([]), new Localizer());
        await destinations.RefreshAsync(default); Assert.True(destinations.NoInstances);
        Assert.Empty(destinations.Capture(false).Targets);
        destinations.ModeIndex = 1; Assert.Throws<InvalidOperationException>(() => destinations.Capture(false));
        var instance = GameInstance.Create("Busy", "26", "Release"); ContentTests.Setup(dir, instance.Id);
        using var busy = activity.Acquire(instance.Id);
        var pack = ContentTests.Zip(dir, ".mcpack", ("manifest.json", ContentTests.Manifest("Safe", "resources")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => destinations.InstallAsync(pack, new(true, [new(instance.Id, null)]), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => destinations.InstallAsync(pack, new(false, [new(instance.Id, null), new(Guid.NewGuid(), null)]), default));
        Assert.Empty(await library.ListAsync());
    }
}

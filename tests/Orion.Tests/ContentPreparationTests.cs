using Orion.Domain;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

public sealed class ContentPreparationTests
{
    [Theory]
    [InlineData("Release", "Minecraft Bedrock")]
    [InlineData("Preview", "Minecraft Bedrock Preview")]
    public async Task FreshInstancePreparesPackStorageWithoutInventingPlayerProfiles(string channel, string edition)
    {
        using var dir = new TestDirectory();
        var instance = GameInstance.Create("Fresh", "26.50", channel);
        await new InstanceRepository(dir.Paths).SaveAsync(instance);
        var service = new InstanceContentService(dir.Paths, new());
        var snapshot = await service.ListAsync(instance.Id);
        Assert.Empty(snapshot.Profiles); Assert.Empty(snapshot.Entries);
        var users = Path.Combine(dir.Paths.Prefix(instance.Id), "drive_c/users", Environment.UserName, "AppData/Roaming", edition, "Users");
        foreach (var pack in new[] { "behavior_packs", "resource_packs" })
            Assert.True(Directory.Exists(Path.Combine(users, "Shared/games/com.mojang", pack)));
        Assert.Single(Directory.GetDirectories(users)); // Shared only: no fabricated XUID.
        await service.ImportAsync(instance.Id, ContentTests.Zip(dir, ".mcpack", ("manifest.json", ContentTests.Manifest("New texture", "resources"))), null);
        Assert.Single((await service.ListAsync(instance.Id)).Entries);
        var world = ContentTests.Zip(dir, ".mcworld", ("level.dat", "fixture"), ("db/001.ldb", "fixture"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(instance.Id, world, null));
        Directory.CreateDirectory(Path.Combine(users, "123456"));
        snapshot = await service.ListAsync(instance.Id);
        var profile = Assert.Single(snapshot.Profiles);
        Assert.True(Directory.Exists(Path.Combine(dir.Paths.Instance(instance.Id), profile.Id, "minecraftWorlds")));
        await service.ImportAsync(instance.Id, world, profile.Id);
        Assert.Equal(2, (await service.ListAsync(instance.Id)).Entries.Count);
        Assert.False(Directory.Exists(Path.Combine(dir.Paths.Game(instance.Id), "behavior_packs")));
    }

    [Fact]
    public async Task RecreatesMissingLeavesButRejectsSymlinkRedirects()
    {
        using var dir = new TestDirectory(); var id = Guid.NewGuid(); var users = ContentTests.Setup(dir, id);
        var service = new InstanceContentService(dir.Paths, new());
        await service.ListAsync(id);
        var packs = Path.Combine(users, "Shared/games/com.mojang/behavior_packs");
        Directory.Delete(packs); await service.ListAsync(id); Assert.True(Directory.Exists(packs));
        Directory.Delete(packs);
        var outside = Directory.CreateDirectory(Path.Combine(dir.Root, "outside")); Directory.CreateSymbolicLink(packs, outside.FullName);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(id)); Assert.Empty(outside.GetFileSystemInfos());
    }
}

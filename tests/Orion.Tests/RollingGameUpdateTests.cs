using Orion.Application;
using Orion.Domain;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

public sealed class RollingGameUpdateTests
{
    private static void Same(GameInstance expected, GameInstance actual) =>
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected), System.Text.Json.JsonSerializer.Serialize(actual));
    private static GameVersion V(string version, string channel) => new(version, channel, new Uri("https://example.test/" + version));
    private sealed class Catalog(params GameVersion[] versions) : IVersionCatalog
    {
        public int Calls;
        public Task<IReadOnlyList<GameVersion>> GetAsync(CancellationToken cancellationToken = default)
        { Calls++; cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<GameVersion>>(versions); }
    }
    private sealed class Installer(AppPaths paths) : IGameUpdateInstaller
    {
        public int Calls;
        public bool Fail;
        public async Task PrepareUpdateAsync(GameInstance instance, string source, IProgress<OperationProgress>? progress, CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new IOException("download failed");
            var next = Path.Combine(paths.Instance(instance.Id), ".update-next");
            SeedGame(Path.Combine(next, "game"), instance.Version);
            await AtomicFile.WriteJsonAsync(Path.Combine(next, "instance.json"), instance, ct);
        }
    }
    private static void SeedGame(string path, string version)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Minecraft.Windows.exe"), version);
        File.WriteAllText(Path.Combine(path, ".xodus-streaming.msixvc"), "metadata");
    }
    private static async Task<GameInstance> Seed(TestDirectory d, bool rolling = true)
    {
        var instance = GameInstance.Create("My world", "1.0", "Release") with { TrackUpdates = rolling, DesktopShortcut = true };
        await new InstanceRepository(d.Paths).SaveAsync(instance);
        SeedGame(d.Paths.Game(instance.Id), instance.Version);
        Directory.CreateDirectory(d.Paths.Prefix(instance.Id));
        File.WriteAllText(Path.Combine(d.Paths.Prefix(instance.Id), "world"), "keep");
        return instance;
    }
    [Fact] public void ChoicesKeepActualVersionsAndSeparateChannels()
    {
        var choices = GameVersion.WithRollingChoices([V("1.9", "Release"), V("1.10", "Release"), V("2.0", "Preview")]);
        Assert.Equal("Latest Release", choices[0].ToString()); Assert.Equal("1.10", choices[0].Version);
        Assert.Equal("Latest Preview", choices[1].ToString()); Assert.Equal("2.0", choices[1].Version);
        Assert.All(choices.Skip(2), v => Assert.False(v.TrackUpdates));
    }
    [Fact] public async Task UpdatePreservesPrefixIdentityAndSettingsAndChecksEveryLaunch()
    {
        using var d = new TestDirectory(); var original = await Seed(d);
        var catalog = new Catalog(V("1.1", "Release"), V("3.0", "Preview")); var installer = new Installer(d.Paths);
        var updater = new RollingGameUpdate(d.Paths, catalog, installer);
        var updated = await updater.PrepareAsync(original, null, default);
        Same(original with { Version = "1.1" }, updated);
        Same(updated, await new InstanceRepository(d.Paths).GetAsync(original.Id));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(d.Paths.Prefix(original.Id), "world")));
        await updater.PrepareAsync(updated, null, default);
        Assert.Equal(2, catalog.Calls); Assert.Equal(1, installer.Calls);
    }
    private sealed class OfflineCatalog : IVersionCatalog
    {
        public Task<IReadOnlyList<GameVersion>> GetAsync(CancellationToken cancellationToken = default) => throw new HttpRequestException("offline");
    }
    [Fact] public async Task OfflineUsesInstalledVersion()
    {
        using var d = new TestDirectory(); var original = await Seed(d);
        Same(original, await new RollingGameUpdate(d.Paths, new OfflineCatalog(), new Installer(d.Paths)).PrepareAsync(original, null, default));
    }
    [Fact] public async Task OlderCatalogNeverDowngrades()
    {
        using var d = new TestDirectory(); var original = await Seed(d);
        var installer = new Installer(d.Paths);
        Same(original, await new RollingGameUpdate(d.Paths, new Catalog(V("0.9", "Release")), installer).PrepareAsync(original, null, default));
        Assert.Equal(0, installer.Calls);
    }
    [Fact] public async Task FixedVersionsDoNotCheckOrUpdate()
    {
        using var d = new TestDirectory(); var original = await Seed(d, false);
        var catalog = new Catalog(V("2.0", "Release")); var installer = new Installer(d.Paths);
        Same(original, await new RollingGameUpdate(d.Paths, catalog, installer).PrepareAsync(original, null, default));
        Assert.Equal(0, catalog.Calls); Assert.Equal(0, installer.Calls);
    }
    [Fact] public async Task FailedPreparationLeavesInstalledGameUntouched()
    {
        using var d = new TestDirectory(); var original = await Seed(d);
        var updater = new RollingGameUpdate(d.Paths, new Catalog(V("2.0", "Release")), new Installer(d.Paths) { Fail = true });
        await Assert.ThrowsAsync<IOException>(() => updater.PrepareAsync(original, null, default));
        Assert.Equal("1.0", File.ReadAllText(Path.Combine(d.Paths.Game(original.Id), "Minecraft.Windows.exe")));
        Same(original, await new InstanceRepository(d.Paths).GetAsync(original.Id));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InterruptedSwapFinishesWithoutRedownloading(bool movedNewGame)
    {
        using var d = new TestDirectory(); var original = await Seed(d);
        var installer = new Installer(d.Paths); var updated = original with { Version = "2.0" };
        await installer.PrepareUpdateAsync(updated, "unused", null, default);
        var root = d.Paths.Instance(original.Id);
        Directory.Move(d.Paths.Game(original.Id), Path.Combine(root, ".update-previous"));
        if (movedNewGame) Directory.Move(Path.Combine(root, ".update-next", "game"), d.Paths.Game(original.Id));
        Same(updated, await new RollingGameUpdate(d.Paths, new Catalog(V("2.0", "Release")), installer).PrepareAsync(original, null, default));
        Assert.Equal(1, installer.Calls);
        Assert.False(Directory.Exists(Path.Combine(root, ".update-previous")));
    }
}

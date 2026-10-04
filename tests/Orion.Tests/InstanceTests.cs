using Orion.Domain;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

public sealed class InstanceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("bad\nname")]
    public void InvalidNamesCannotCreateInstances(string name) => Assert.Throws<ArgumentException>(() => GameInstance.Create(name, "1.0", "Release"));

    [Fact]
    public async Task RenamePreservesIdentityAndArchivePreservesWorlds()
    {
        using var directory = new TestDirectory();
        var repository = new InstanceRepository(directory.Paths);
        var instance = GameInstance.Create("Survival / friends", "1.26.0", "Release");
        await repository.SaveAsync(instance);
        var save = Path.Combine(directory.Paths.Instance(instance.Id), "world.dat");
        await File.WriteAllTextAsync(save, "precious world");
        await repository.SaveAsync(instance with { Name = "Renamed" });
        Assert.Equal("Renamed", (await repository.GetAsync(instance.Id)).Name);
        Assert.Single(await repository.ListAsync());
        await repository.ArchiveAsync(instance.Id);
        Assert.Empty(await repository.ListAsync());
        Assert.Equal("precious world", await File.ReadAllTextAsync(Directory.GetFiles(directory.Paths.Archives, "world.dat", SearchOption.AllDirectories).Single()));
    }

    [Fact]
    public async Task MetadataMismatchIsReported()
    {
        using var directory = new TestDirectory();
        var instance = GameInstance.Create("Test", "1.0", "Release");
        await AtomicFile.WriteJsonAsync(Path.Combine(directory.Paths.Instance(Guid.NewGuid()), "instance.json"), instance);
        await Assert.ThrowsAsync<InvalidDataException>(() => new InstanceRepository(directory.Paths).ListAsync());
    }

    [Fact]
    public void CatalogKeepsChannelsAndSortsVersionsNumerically()
    {
        var versions = VersionCatalog.Parse("""
            {"releaseVersions":[{"version":"Release 1.9.0","urls":["https://example.test/a"]},{"version":"Release 1.10.0","urls":["https://example.test/b"]}],
            "previewVersions":[{"version":"Preview 1.11.0","urls":["https://example.test/c"]}]}
            """);
        Assert.Equal(new[] { "1.11.0", "1.10.0", "1.9.0" }, versions.Select(v => v.Version));
        Assert.Equal("Preview", versions[0].Channel);
    }

    [Fact]
    public void IncompleteXodusExtractionIsNotAccepted()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(Path.Combine(directory.Root, "Minecraft.Windows.exe"), "partial");
        Assert.Throws<InvalidDataException>(() => GameLayout.Validate(directory.Root));
    }

    [Theory]
    [InlineData("http://assets1.xboxlive.com/game.msixvc", true)]
    [InlineData("http://assets2.xboxlive.com/game.msixvc", true)]
    [InlineData("http://assets1.xboxlive.com.evil.test/game.msixvc", false)]
    [InlineData("http://assets1.xboxlive.com:8080/game.msixvc", false)]
    [InlineData("http://unknown.test/game.msixvc", false)]
    [InlineData("https://example.test/game.msixvc", true)]
    [InlineData("file:///tmp/game.msixvc", false)]
    public void CatalogAcceptsTheOfficialHttpCdnOnly(string url, bool accepted) =>
        Assert.Equal(accepted, GamePackageSource.IsSupported(new(url)));
}

using System.Text.Json.Nodes;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Rtx;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

public sealed class RtxCompatibilityTests
{
    [Fact]
    public async Task NewerGameAcceptancePersistsOnlyForInstalledPresetAndExactInstanceVersion()
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir);
        instance = instance with { Version = "26.52.03" };
        await new InstanceRepository(dir.Paths).SaveAsync(instance);
        var activity = new InstanceActivity(); var catalog = new RtxTests.Catalog();
        var service = new RtxService(dir.Paths, activity, catalog, new(dir.Paths, activity));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(instance, RtxTests.Preset(), false, null, CancellationToken.None));
        await service.InstallAsync(instance, RtxTests.Preset(), true, null, CancellationToken.None);
        var state = await service.InspectAsync(instance.Id);
        Assert.Null(state.CompatibilityError);
        Assert.Equal(instance.Version, state.Installation!.AcceptedNewerGameVersion);
        // A fresh service must honor the persisted acknowledgement without silently reusing it for a new preset.
        service = new(dir.Paths, activity, catalog, new(dir.Paths, activity));
        service.ValidateForLaunchUnderLease(instance.Id);
        service.PrepareLaunchUnderLease(instance.Id, null, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(instance, RtxTests.Preset("Another preset"), false, null, CancellationToken.None));
        await new InstanceRepository(dir.Paths).SaveAsync(instance with { Version = "26.53.0" });
        Assert.NotNull((await service.InspectAsync(instance.Id)).CompatibilityError);
        Assert.Throws<InvalidOperationException>(() => service.ValidateForLaunchUnderLease(instance.Id));
        await service.RestoreAsync(instance.Id);
        service.ValidateForLaunchUnderLease(instance.Id);
        Assert.Null((await service.InspectAsync(instance.Id)).Installation);
    }

    [Theory]
    [InlineData("26.30")]
    [InlineData("unknown")]
    public async Task AcknowledgementNeverPermitsAnOlderOrUnknownGame(string version)
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir);
        instance = instance with { Version = version };
        await new InstanceRepository(dir.Paths).SaveAsync(instance);
        var activity = new InstanceActivity(); var service = new RtxService(dir.Paths, activity, new RtxTests.Catalog(), new(dir.Paths, activity));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(instance, RtxTests.Preset(), true, null, CancellationToken.None));
        Assert.Null((await service.InspectAsync(instance.Id)).Installation);
    }

    [Theory]
    [InlineData("26.52.03")]
    [InlineData("26.40.27")]
    [InlineData("26.40.25")]
    [InlineData("unknown")]
    public async Task ShaderMismatchCannotBeForcedWithStaleCallerOrAcceptance(string version)
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir);
        var activity = new InstanceActivity(); var service = new RtxService(dir.Paths, activity, new RtxTests.Catalog(), new(dir.Paths, activity));
        await new InstanceRepository(dir.Paths).SaveAsync(instance with { Version = version });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(instance, RtxTests.Preset(), true, null, CancellationToken.None));
        Assert.Null((await service.InspectAsync(instance.Id)).Installation);
    }

    [Fact]
    public async Task UpdatingGameBlocksPreviouslyInstalledShadersButKeepsRecoveryAvailable()
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir);
        var activity = new InstanceActivity(); var service = new RtxService(dir.Paths, activity, new RtxTests.Catalog(), new(dir.Paths, activity));
        await service.InstallAsync(instance, RtxTests.Preset(), false, null, CancellationToken.None);
        await new InstanceRepository(dir.Paths).SaveAsync(instance with { Version = "26.52.03" });
        var state = await service.InspectAsync(instance.Id);
        Assert.NotNull(state.CompatibilityError); Assert.True(state.CanRestore);
        Assert.Throws<InvalidOperationException>(() => service.ValidateForLaunchUnderLease(instance.Id));
        Assert.Throws<InvalidOperationException>(() => service.PrepareLaunchUnderLease(instance.Id, null, CancellationToken.None));
        await service.RestoreAsync(instance.Id);
        service.ValidateForLaunchUnderLease(instance.Id);
        Assert.Null((await service.InspectAsync(instance.Id)).CompatibilityError);
    }

    [Fact]
    public async Task UntargetedArchiveAndCreatorOutputAreBlockedEvenWithAcceptance()
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir);
        var activity = new InstanceActivity(); var service = new RtxService(dir.Paths, activity, new RtxTests.Catalog(), new(dir.Paths, activity));
        var archive = ContentTests.Zip(dir, ".rtpack", ("notes.txt", "No target declared"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(instance, archive, true, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(instance, RtxTests.Preset() with { GameVersion = null }, true, null, CancellationToken.None));
        Assert.Null((await service.InspectAsync(instance.Id)).Installation);
    }

    [Fact]
    public void UrlInferredTargetsRequireAgreementAcrossAllThreeMaterials()
    {
        Assert.True(Assert.Single(RtxCatalog.Parse(RtxTests.CatalogJson)).VersionFromAssetPath);
        var mixed = RtxTests.CatalogJson.Replace("v26.40.26/default/bloom", "v26.30/default/bloom");
        var preset = Assert.Single(RtxCatalog.Parse(mixed));
        Assert.Null(preset.GameVersion); Assert.False(preset.VersionFromAssetPath);
        var declaredConflict = mixed.Replace("\"name\":\"Daylight\"", "\"name\":\"Daylight\",\"gameVersion\":\"26.40.26\"");
        Assert.Null(Assert.Single(RtxCatalog.Parse(declaredConflict)).GameVersion);
    }

    private static RtxTests.Catalog TextureCatalog(TestDirectory dir, string? minimum)
    {
        var manifest = JsonNode.Parse(ContentTests.Manifest("Opus", "resources", RtxTests.Opus().PackId))!;
        // Deliberately unrelated pack version: compatibility must use min_engine_version.
        manifest["header"]!["version"] = new JsonArray(99, 99, 99);
        if (minimum is not null) manifest["header"]!["min_engine_version"] = new JsonArray(minimum.Split('.').Select(v => (JsonNode?)JsonValue.Create(int.Parse(v))).ToArray());
        return new() { TextureArchive = ContentTests.Zip(dir, ".mcpack", ("manifest.json", manifest.ToJsonString())) };
    }

    [Theory]
    [InlineData("1.26.52")]
    [InlineData(null)]
    public async Task VanillaNewerOrUnknownRequirementNeedsExplicitAcknowledgement(string? minimum)
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir);
        var activity = new InstanceActivity(); var content = new InstanceContentService(dir.Paths, activity);
        var service = new RtxService(dir.Paths, activity, TextureCatalog(dir, minimum), content);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.InstallTextureAsync(instance.Id, RtxTests.Opus(), null, CancellationToken.None));
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.InstallTextureAsync(instance.Id, RtxTests.Opus(), null, CancellationToken.None, (_, _) => Task.FromResult(false)));
        Assert.Empty((await content.ListAsync(instance.Id)).Entries);
        var acknowledgements = 0;
        await service.InstallTextureAsync(instance.Id, RtxTests.Opus(), null, CancellationToken.None, (required, game) =>
        {
            Assert.Equal(minimum ?? "", required); Assert.Equal(instance.Version, game); acknowledgements++;
            return Task.FromResult(true);
        });
        Assert.Equal(1, acknowledgements); Assert.Single((await content.ListAsync(instance.Id)).Entries);
        Assert.Empty(Directory.GetDirectories(Path.Combine(dir.Paths.Cache, "rtx"), "pack-*"));
    }

    [Theory]
    [InlineData("1.26.40")]
    [InlineData("1.26.40.26")]
    [InlineData("1.26.30")]
    public async Task VanillaSupportedMinimumDoesNotPromptBasedOnPackVersion(string minimum)
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir);
        var activity = new InstanceActivity(); var service = new RtxService(dir.Paths, activity, TextureCatalog(dir, minimum), new(dir.Paths, activity));
        await service.InstallTextureAsync(instance.Id, RtxTests.Opus(), null, CancellationToken.None, (_, _) => throw new Exception("Unexpected acknowledgement"));
        Assert.Single(await service.InstalledTexturesAsync(instance.Id));
    }

    [Fact]
    public async Task VanillaVersionChangeDuringAcknowledgementRequiresFreshReview()
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir);
        var activity = new InstanceActivity(); var content = new InstanceContentService(dir.Paths, activity);
        var service = new RtxService(dir.Paths, activity, TextureCatalog(dir, "1.26.52"), content);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallTextureAsync(instance.Id, RtxTests.Opus(), null, CancellationToken.None, async (_, _) =>
        {
            await new InstanceRepository(dir.Paths).SaveAsync(instance with { Version = "26.30.0" });
            return true;
        }));
        Assert.Empty((await content.ListAsync(instance.Id)).Entries);
    }
}

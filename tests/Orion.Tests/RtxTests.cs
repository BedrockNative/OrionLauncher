using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orion.Application;
using Orion.Domain;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Rtx;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

public sealed class RtxTests
{
    internal static byte[] Shader(byte marker) => [0x1a, 0xda, 0x11, 0x0a, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, marker];
    internal static RtxPreset Preset(string name = "Daylight") => new("default", name, "v26.40.26", "1.4.4", null,
        new Dictionary<string, Uri> { ["RTXStub"] = new("https://cdn.bedrock.graphics/example/RTXStub.material.bin") });
    internal sealed class Catalog : IRtxCatalog
    {
        public bool UsedCache => false;
        public int Requests { get; private set; }
        public bool CancelDownload { get; set; }
        public string? TextureArchive { get; set; }
        public IReadOnlyList<RtxTexturePack> Textures { get; set; } = [];
        public Task<IReadOnlyList<RtxPreset>> PresetsAsync(CancellationToken ct) { Requests++; return Task.FromResult<IReadOnlyList<RtxPreset>>([Preset(), Preset("Cinematic") with { Id = "cinematic" }]); }
        public Task<IReadOnlyList<RtxTexturePack>> TexturesAsync(CancellationToken ct) => Task.FromResult(Textures);
        public Task<IReadOnlyList<RtxCreatorVersion>> CreatorVersionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RtxCreatorVersion>>([new("1.4.4", "BetterRTX 1.4.4", true)]);
        public Task<RtxCreatorForm> CreatorFormAsync(string version, CancellationToken ct) => Task.FromResult(RtxCreatorForm.Parse(RtxParityTests.FormJson));
        public Task<RtxPreset> BuildCreatorAsync(string version, RtxCreatorForm form, IReadOnlyDictionary<string, JsonElement> values, IProgress<OperationProgress>? progress, CancellationToken ct)
        { _ = form.SerializeSettings(values); return Task.FromResult(Preset("Custom") with { Id = "build-test", GameVersion = null }); }
        public async Task DownloadPresetAsync(RtxPreset preset, string directory, IProgress<OperationProgress>? progress, CancellationToken ct)
        {
            foreach (var name in preset.Materials.Keys)
                await File.WriteAllBytesAsync(Path.Combine(directory, name + ".material.bin"), Shader(9), ct);
            if (CancelDownload) throw new OperationCanceledException();
        }
        public Task DownloadTextureAsync(RtxTexturePack pack, string path, IProgress<OperationProgress>? progress, CancellationToken ct)
        { File.Copy(TextureArchive ?? throw new NotSupportedException(), path); return Task.CompletedTask; }
    }
    internal static async Task<GameInstance> Setup(TestDirectory directory)
    {
        var instance = GameInstance.Create("RTX world", "26.40.26", "Release");
        await new InstanceRepository(directory.Paths).SaveAsync(instance);
        var game = directory.Paths.Game(instance.Id); var materials = Path.Combine(game, "data/renderer/materials"); Directory.CreateDirectory(materials);
        await File.WriteAllTextAsync(Path.Combine(game, "Minecraft.Windows.exe"), "fixture");
        foreach (var name in RtxCatalog.MaterialNames) await File.WriteAllBytesAsync(Path.Combine(materials, name + ".material.bin"), Shader(1));
        var entries = RtxCatalog.MaterialNames.Select(n => new { name = n == "RTXPostFX.Tonemapping" ? "RTXPostFX.ToneMapping" : n, path = n })
            .Append(new { name = "Terrain", path = "Terrain" });
        await File.WriteAllTextAsync(Path.Combine(materials, "materials.index.json"), JsonSerializer.Serialize(new { materials = entries, untouched = 42 }));
        return instance;
    }
    private static RtxService Service(TestDirectory dir, InstanceActivity activity, IRtxCatalog? catalog = null) => new(dir.Paths, activity, catalog ?? new Catalog(), new(dir.Paths, activity));
    private static string Materials(TestDirectory dir, GameInstance instance) => Path.Combine(dir.Paths.Game(instance.Id), "data/renderer/materials");

    internal static RtxTexturePack Opus() => new("Vanilla RTX Opus 1.26.22", "release-test",
        new(1, "Vanilla-RTX-Opus-1.26.22.mcpack", new("https://github.com/Cubeir/Vanilla-RTX/releases/download/test/Vanilla-RTX-Opus-1.26.22.mcpack"), 100, null),
        RtxCatalog.TexturePackId("Vanilla-RTX-Opus-1.26.22.mcpack"));

    [Fact]
    public async Task OfficialThreeSlotSetupIsVerifiedAndDetectsExternalIndexChanges()
    {
        using var dir = new TestDirectory(); var instance = await Setup(dir); var service = Service(dir, new());
        var files = new List<string>();
        foreach (var name in new[] { "RTXStub", "RTXPostFX.Bloom", "RTXPostFX.ToneMapping" })
        {
            var file = Path.Combine(dir.Root, name + ".material.bin"); await File.WriteAllBytesAsync(file, Shader(7)); files.Add(file);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportMaterialsAsync(instance, files, true, CancellationToken.None));
        await service.InstallAsync(instance, Preset() with { Materials = new[] { "RTXStub", "RTXPostFX.Bloom", "RTXPostFX.Tonemapping" }.ToDictionary(n => n, n => new Uri("https://cdn.bedrock.graphics/" + n)) }, false, null, CancellationToken.None);
        var state = await service.InspectAsync(instance.Id); Assert.Null(state.VerificationError);
        var installed = state.Installation!; Assert.Equal(3, installed.Hashes.Count);
        var indexPath = Path.Combine(Materials(dir, instance), "materials.index.json");
        using (var index = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath)))
        {
            foreach (var slot in index.RootElement.GetProperty("materials").EnumerateArray())
            {
                var name = slot.GetProperty("name").GetString()!;
                var canonical = RtxCatalog.MaterialNames.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                var expected = canonical is not null && installed.Hashes.ContainsKey(canonical) ? installed.Folder + "/" + canonical : name;
                Assert.Equal(expected, slot.GetProperty("path").GetString());
            }
        }
        foreach (var name in RtxCatalog.MaterialNames)
            Assert.Equal(Shader(1), await File.ReadAllBytesAsync(Path.Combine(Materials(dir, instance), name + ".material.bin")));
        var correct = await File.ReadAllTextAsync(indexPath);
        await File.WriteAllTextAsync(indexPath, correct.Replace(installed.Folder + "/RTXStub", "RTXStub"));
        Assert.NotNull((await service.InspectAsync(instance.Id)).VerificationError);
        await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(instance.Id));
        await File.WriteAllTextAsync(indexPath, correct); await service.RestoreAsync(instance.Id);
        Assert.Null((await service.InspectAsync(instance.Id)).Installation);
    }

    [Fact]
    public async Task LocalMaterialImportRejectsInvalidDuplicateAndLinkedInputsWithoutChangingSetup()
    {
        using var dir = new TestDirectory(); var instance = await Setup(dir); var service = Service(dir, new());
        var file = Path.Combine(dir.Root, "RTXStub.material.bin"); await File.WriteAllBytesAsync(file, Shader(2));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportMaterialsAsync(instance, [file, file], true, CancellationToken.None));
        await File.WriteAllTextAsync(file, "not a compiled shader");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportMaterialsAsync(instance, [file], true, CancellationToken.None));
        var link = Path.Combine(dir.Root, "RTXPostFX.material.bin"); File.CreateSymbolicLink(link, file);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportMaterialsAsync(instance, [link], true, CancellationToken.None));
        Assert.Null((await service.InspectAsync(instance.Id)).Installation);
        Assert.Empty(Directory.GetDirectories(Path.Combine(dir.Paths.Instance(instance.Id), "rtx"), "download-*"));
    }

    [Fact]
    public async Task TextureDetectionUsesActualPackUuidAndExcludesArchivedPacks()
    {
        using var dir = new TestDirectory(); var instance = await Setup(dir); var activity = new InstanceActivity();
        var content = new InstanceContentService(dir.Paths, activity); var service = Service(dir, activity);
        var pack = Opus();
        var archive = ContentTests.Zip(dir, ".mcpack", ("manifest.json", ContentTests.Manifest("Renamed Opus", "resources", pack.PackId)));
        await content.ImportAsync(instance.Id, archive, null);
        var installed = Assert.Single(await service.InstalledTexturesAsync(instance.Id)); Assert.Equal(pack.PackId, installed.PackId);
        // Fails before the stub catalog's unimplemented downloader, not after downloading a duplicate.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallTextureAsync(instance.Id, pack, null, CancellationToken.None));
        await content.ArchiveAsync(instance.Id, installed.Id); Assert.Empty(await service.InstalledTexturesAsync(instance.Id));
        Assert.Null(RtxCatalog.TexturePackId("Vanilla-RTX-"));
        Assert.Null(RtxCatalog.TexturePackId("Vanilla-RTX-Unknown.mcpack"));
    }

    [Theory]
    [InlineData("v26.40.26", "26.40.03", RtxCompatibility.OlderGame)]
    [InlineData("v26.40.26", "26.40.26", RtxCompatibility.Matching)]
    [InlineData("v26.40.26", "26.40.27", RtxCompatibility.NewerGame)]
    [InlineData("1.26.40", "26.40.0", RtxCompatibility.Matching)]
    [InlineData("1.26.40", "26.30.0", RtxCompatibility.OlderGame)]
    [InlineData("1.26.40", "26.52.03", RtxCompatibility.NewerGame)]
    [InlineData("26.40.26", "1.21.0.3", RtxCompatibility.OlderGame)]
    [InlineData(null, "26.40.03", RtxCompatibility.Unknown)]
    [InlineData("unknown", "26.40.03", RtxCompatibility.Unknown)]
    public void VersionsCompareAllComponentsWithoutAssumingUnknownIsCompatible(string? preset, string game, RtxCompatibility expected)
        => Assert.Equal(expected, RtxCatalog.Compatibility(preset, game));

    [Fact]
    public async Task InstallSwitchAndRestoreKeepStockBytesAndUnrelatedIndexEntries()
    {
        using var dir = new TestDirectory(); var instance = await Setup(dir); var activity = new InstanceActivity(); var service = Service(dir, activity);
        await service.InstallAsync(instance, Preset(), false, null, CancellationToken.None);
        var first = (await service.InspectAsync(instance.Id)).Installation!;
        var index = Path.Combine(Materials(dir, instance), "materials.index.json");
        Assert.Contains(first.Folder + "/RTXStub", await File.ReadAllTextAsync(index));
        Assert.Equal(Shader(1), await File.ReadAllBytesAsync(Path.Combine(Materials(dir, instance), "RTXStub.material.bin")));
        await service.InstallAsync(instance, Preset("Evening"), false, null, CancellationToken.None);
        Assert.False(Directory.Exists(Path.Combine(Materials(dir, instance), first.Folder)));
        Assert.Equal("Evening", (await service.InspectAsync(instance.Id)).Installation!.Name);
        await service.RestoreAsync(instance.Id);
        Assert.Null((await service.InspectAsync(instance.Id)).Installation);
        using var restored = JsonDocument.Parse(await File.ReadAllTextAsync(index));
        Assert.Equal(42, restored.RootElement.GetProperty("untouched").GetInt32());
        Assert.All(restored.RootElement.GetProperty("materials").EnumerateArray(), e => Assert.DoesNotContain('/', e.GetProperty("path").GetString()!));
        Assert.Empty(Directory.GetDirectories(Materials(dir, instance), "orion-rtx-*"));
    }
    [Fact]
    public async Task CancellationAndInterruptedDownloadRemoveOnlyWorkFiles()
    {
        using var dir = new TestDirectory(); var instance = await Setup(dir); var activity = new InstanceActivity(); var catalog = new Catalog { CancelDownload = true }; var service = Service(dir, activity, catalog);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.InstallAsync(instance, Preset(), false, null, CancellationToken.None));
        Assert.Empty(Directory.GetDirectories(Path.Combine(dir.Paths.Instance(instance.Id), "rtx"), "download-*"));
        var interrupted = Path.Combine(dir.Paths.Instance(instance.Id), "rtx/download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(interrupted);
        await File.WriteAllTextAsync(Path.Combine(interrupted, "partial.bin"), "partial");
        Assert.Null((await service.InspectAsync(instance.Id)).Installation); Assert.False(Directory.Exists(interrupted));
    }
    [Fact]
    public async Task RunningLeaseAndExternalEditsBlockMutation()
    {
        using var dir = new TestDirectory(); var instance = await Setup(dir); var activity = new InstanceActivity(); var service = Service(dir, activity);
        using (activity.Acquire(instance.Id))
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(instance, Preset(), false, null, CancellationToken.None));
        await service.InstallAsync(instance, Preset(), false, null, CancellationToken.None);
        var receipt = (await service.InspectAsync(instance.Id)).Installation!;
        var material = Path.Combine(Materials(dir, instance), receipt.Folder, "RTXStub.material.bin"); await File.WriteAllBytesAsync(material, Shader(7));
        await Assert.ThrowsAsync<IOException>(() => service.InstallAsync(instance, Preset(), false, null, CancellationToken.None));
        Assert.Throws<IOException>(() => service.ValidateForLaunchUnderLease(instance.Id));
        Assert.Throws<IOException>(() => service.PrepareLaunchUnderLease(instance.Id, null, default));
        Assert.True((await service.InspectAsync(instance.Id)).CanRestore);
        await service.RestoreAsync(instance.Id);
        Assert.Equal(Shader(7), await File.ReadAllBytesAsync(Path.Combine(dir.Paths.Instance(instance.Id), "rtx/quarantine", receipt.Folder, "RTXStub.material.bin")));
        service.ValidateForLaunchUnderLease(instance.Id);
    }
    [Fact]
    public async Task UnsupportedOrUnconfirmedVersionsNeverInstall()
    {
        using var dir = new TestDirectory(); var instance = await Setup(dir); var service = Service(dir, new());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(instance, Preset() with { GameVersion = "26.52.03" }, true, null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(instance, Preset() with { GameVersion = null }, false, null, CancellationToken.None));
    }
    [Fact]
    public async Task ImportRejectsTraversalAndInstallsOnlyAllowedMaterials()
    {
        using var dir = new TestDirectory(); var instance = await Setup(dir); var service = Service(dir, new());
        var archive = Path.Combine(dir.Root, "custom.rtpack");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var shader = zip.CreateEntry("custom/RTXStub.material.bin").Open()) shader.Write(Shader(8));
            using var metadata = new StreamWriter(zip.CreateEntry("orion-preset.json").Open());
            metadata.Write(JsonSerializer.Serialize(new { gameVersion = instance.Version }));
        }
        await service.ImportAsync(instance, archive, true, CancellationToken.None);
        Assert.Equal("custom", (await service.InspectAsync(instance.Id)).Installation!.Name);
        var unsafeArchive = Path.Combine(dir.Root, "unsafe.rtpack");
        using (var zip = ZipFile.Open(unsafeArchive, ZipArchiveMode.Create)) { using var file = zip.CreateEntry("../escape").Open(); file.Write([1]); }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(instance, unsafeArchive, true, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(dir.Root, "escape")));
    }
    [Fact]
    public async Task SymlinkedGameTreeIsRejected()
    {
        using var dir = new TestDirectory(); var instance = await Setup(dir); var service = Service(dir, new());
        var outside = Path.Combine(dir.Root, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(Materials(dir, instance), "external"), outside);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(instance, Preset(), false, null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(outside));
    }
    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("dangling")]
    [InlineData("root")]
    public async Task LaunchScanRejectsEveryKindOfLinkedGamePath(string kind)
    {
        using var dir = new TestDirectory(); var instance = await Setup(dir); var service = Service(dir, new());
        Assert.False(service.PrepareLaunchUnderLease(instance.Id, null, default));
        var game = dir.Paths.Game(instance.Id);
        var outside = Path.Combine(dir.Root, "outside"); Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "marker.txt"); await File.WriteAllTextAsync(marker, "unchanged");
        var link = Path.Combine(game, "linked");
        if (kind == "root")
        {
            var moved = Path.Combine(dir.Root, "moved-game"); Directory.Move(game, moved);
            Directory.CreateSymbolicLink(game, moved);
        }
        else if (kind == "directory") Directory.CreateSymbolicLink(link, outside);
        else File.CreateSymbolicLink(link, kind == "file" ? marker : Path.Combine(outside, "missing"));
        Assert.Throws<InvalidDataException>(() => service.PrepareLaunchUnderLease(instance.Id, null, default));
        Assert.Equal("unchanged", await File.ReadAllTextAsync(marker));
    }

    [Fact]
    public async Task LaunchScanDoesNotCacheIndexChangesBetweenLaunches()
    {
        using var dir = new TestDirectory(); var instance = await Setup(dir); var service = Service(dir, new());
        Assert.False(service.PrepareLaunchUnderLease(instance.Id, null, default));
        var index = Path.Combine(Materials(dir, instance), "materials.index.json");
        var original = await File.ReadAllTextAsync(index);
        var changed = original.Replace("\"path\":\"RTXStub\"", "\"path\":\"external/RTXStub\"");
        Assert.NotEqual(original, changed);
        await File.WriteAllTextAsync(index, changed);
        Assert.Throws<IOException>(() => service.PrepareLaunchUnderLease(instance.Id, null, default));
        await File.WriteAllTextAsync(index, original);
        Assert.False(service.PrepareLaunchUnderLease(instance.Id, null, default));
    }

    [Fact]
    public async Task LaunchScanRetainsTheDirectoryDepthLimit()
    {
        using var dir = new TestDirectory(); var instance = await Setup(dir); var service = Service(dir, new());
        var nested = Path.Combine(new[] { dir.Paths.Game(instance.Id) }.Concat(Enumerable.Repeat("nested", 42)).ToArray());
        Directory.CreateDirectory(nested); await File.WriteAllTextAsync(Path.Combine(nested, "asset"), "fixture");
        Assert.Throws<InvalidDataException>(() => service.PrepareLaunchUnderLease(instance.Id, null, default));
    }

    [Fact]
    public async Task InterruptedTransactionRestoresOriginalIndexBeforeLaunch()
    {
        using var dir = new TestDirectory(); var instance = await Setup(dir); var service = Service(dir, new());
        var root = dir.Paths.Instance(instance.Id); var target = Path.Combine(Materials(dir, instance), "materials.index.json");
        var before = await File.ReadAllBytesAsync(target); var after = Encoding.UTF8.GetBytes("interrupted");
        var journal = Path.Combine(root, "rtx/transaction"); Directory.CreateDirectory(journal);
        await File.WriteAllBytesAsync(Path.Combine(journal, "0.before"), before);
        await File.WriteAllTextAsync(Path.Combine(journal, "changes.json"), JsonSerializer.Serialize(new[] { new { Path = Path.GetRelativePath(root, target), Before = Convert.ToHexString(SHA256.HashData(before)), After = Convert.ToHexString(SHA256.HashData(after)) } }));
        await File.WriteAllBytesAsync(target, after);
        service.PrepareLaunchUnderLease(instance.Id, null, CancellationToken.None);
        Assert.Equal(before, await File.ReadAllBytesAsync(target)); Assert.False(Directory.Exists(journal));
    }
    [Fact]
    public async Task LaunchPreferencesAffectExistingOptionsOnlyAndRemainOptIn()
    {
        using var dir = new TestDirectory(); var instance = await Setup(dir); var service = Service(dir, new());
        var options = Path.Combine(dir.Paths.Prefix(instance.Id), "drive_c/users/player/AppData/Roaming/Minecraft Bedrock/Users/profile/games/com.mojang/minecraftpe/options.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(options)!);
        var original = "volume:0.5\r\ngraphics_mode:1\r\ngraphics_mode:2\r\ngfx_vsync:1\r\n"; await File.WriteAllTextAsync(options, original);
        Assert.False(service.PrepareLaunchUnderLease(instance.Id, null, CancellationToken.None)); Assert.Equal(original, await File.ReadAllTextAsync(options));
        await service.ConfigureAsync(instance.Id, new(true, false)); service.PrepareLaunchUnderLease(instance.Id, null, CancellationToken.None);
        var result = await File.ReadAllTextAsync(options); Assert.Contains("volume:0.5\r\n", result); Assert.Contains("graphics_mode:3\r\n", result); Assert.Contains("gfx_vsync:1", result);
        Assert.Equal(1, result.Split("graphics_mode:").Length - 1);
        await service.ConfigureAsync(instance.Id, new(true, true)); service.PrepareLaunchUnderLease(instance.Id, null, CancellationToken.None); Assert.Contains("gfx_vsync:0", await File.ReadAllTextAsync(options));
        var unchanged = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(options, unchanged);
        Assert.True(service.PrepareLaunchUnderLease(instance.Id, null, CancellationToken.None));
        Assert.Equal(unchanged, File.GetLastWriteTimeUtc(options));
    }
    internal const string CatalogJson = """
      [{"uuid":"default","name":"Daylight","icon":"https://bedrock.graphics/favicon.png",
      "stub":"https://cdn.bedrock.graphics/presets/v26.40.26/default/RTXStub.material.bin",
      "bloom":"https://cdn.bedrock.graphics/presets/v26.40.26/default/bloom","tonemapping":"https://cdn.bedrock.graphics/presets/v26.40.26/default/tone"}]
      """;
    [Fact]
    public void CatalogRejectsUnsafeUrlsAndFindsOlderVersionMetadata()
    {
        var entry = Assert.Single(RtxCatalog.Parse(CatalogJson)); Assert.Equal("26.40.26", entry.GameVersion); Assert.Equal(3, entry.Materials.Count);
        Assert.Throws<InvalidDataException>(() => RtxCatalog.Parse(CatalogJson.Replace("cdn.bedrock.graphics", "evil.test")));
        Assert.False(RtxCatalog.IsAssetUri(new("https://bedrock.graphics.evil.test/a")));
        Assert.False(RtxCatalog.IsAssetUri(new("https://user@bedrock.graphics/a")));
        Assert.Throws<InvalidDataException>(() => RtxCatalog.ValidateMaterial("<html>not a shader</html>"u8));
    }
    [Fact]
    public async Task CatalogUsesValidatedOfflineCacheAndDownloadsRealContract()
    {
        using var dir = new TestDirectory(); var offline = false;
        using var http = new HttpClient(new ReleaseTests.Handler(request =>
        {
            if (offline) throw new HttpRequestException("offline");
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = request.RequestUri!.AbsolutePath == "/api" ? new StringContent(CatalogJson) : new ByteArrayContent(Shader(8)) };
        }));
        var catalog = new RtxCatalog(http, new GitHubReleaseClient(http, dir.Paths.Cache), Path.Combine(dir.Paths.Cache, "rtx"));
        var preset = Assert.Single(await catalog.PresetsAsync(CancellationToken.None)); Assert.False(catalog.UsedCache);
        await catalog.DownloadPresetAsync(preset, Path.Combine(dir.Root, "download"), null, CancellationToken.None); Assert.Equal(3, Directory.GetFiles(Path.Combine(dir.Root, "download")).Length);
        offline = true; Assert.Single(await catalog.PresetsAsync(CancellationToken.None)); Assert.True(catalog.UsedCache);
    }
}

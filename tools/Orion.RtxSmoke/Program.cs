using System.Security.Cryptography;
using System.Text.Json;
using Orion.Domain;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Rtx;
using Orion.Infrastructure.Storage;

// Live read/download validation. Never resolves AppPaths.Discover or touches real game instances.
if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("RTX validation requires Linux.");
var root = Path.Combine(Path.GetTempPath(), "orion-rtx-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine("Isolated RTX fixture: " + root);
try
{
    var paths = new AppPaths(Path.Combine(root, "data"), Path.Combine(root, "config"), Path.Combine(root, "cache"), Path.Combine(root, "runtime"), Path.Combine(root, "applications"));
    paths.EnsureDirectories();
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
    using var downloads = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    var catalog = new RtxCatalog(http, new GitHubReleaseClient(downloads, paths.Cache), Path.Combine(paths.Cache, "rtx"), downloads);
    var presets = await catalog.PresetsAsync(CancellationToken.None);
    Console.WriteLine($"Catalog: {presets.Count} valid presets.");
    var versions = await catalog.CreatorVersionsAsync(CancellationToken.None);
    foreach (var version in versions)
    {
        var form = await catalog.CreatorFormAsync(version.Id, CancellationToken.None);
        _ = form.SerializeSettings(form.Defaults());
        Console.WriteLine($"Creator {version.Id}: {form.Categories.Count} categories, {form.Fields.Count} validated settings.");
    }
    if (args.Contains("--compile"))
    {
        var version = versions.First(v => v.IsDefault); var form = await catalog.CreatorFormAsync(version.Id, CancellationToken.None);
        try
        {
            var built = await catalog.BuildCreatorAsync(version.Id, form, form.Defaults(), new Progress<OperationProgress>(p => Console.WriteLine(p.Message)), CancellationToken.None);
            await catalog.DownloadPresetAsync(built, Path.Combine(root, "compiled"), null, CancellationToken.None);
            Console.WriteLine("PASS: official creator compiled and returned three valid materials.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("anti-bot")) { Console.WriteLine("UPSTREAM LIMITATION: " + ex.Message); }
    }
    if (args.Contains("--dlss"))
    {
        var source = await catalog.DownloadDlssAsync(Path.Combine(root, "nvngx_dlss.dll"), CancellationToken.None);
        Console.WriteLine("PASS: official x64 DLSS download · " + source);
    }
    var selected = presets.First(p => p.Id == "default");
    var instance = GameInstance.Create("Disposable RTX validation", selected.GameVersion!.TrimStart('v'), "Release");
    await new InstanceRepository(paths).SaveAsync(instance);
    var materials = Path.Combine(paths.Game(instance.Id), "data/renderer/materials"); Directory.CreateDirectory(materials);
    await File.WriteAllTextAsync(Path.Combine(paths.Game(instance.Id), "Minecraft.Windows.exe"), "Non-executable test fixture");
    var original = new byte[] { 0x1a, 0xda, 0x11, 0x0a, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 1 };
    foreach (var name in RtxCatalog.MaterialNames) await File.WriteAllBytesAsync(Path.Combine(materials, name + ".material.bin"), original);
    await File.WriteAllTextAsync(Path.Combine(materials, "materials.index.json"), JsonSerializer.Serialize(new { materials = RtxCatalog.MaterialNames.Select(n => new { name = n, path = n }) }));
    var activity = new InstanceActivity(); var content = new InstanceContentService(paths, activity); var service = new RtxService(paths, activity, catalog, content);
    await service.InstallAsync(instance, selected, false, null, CancellationToken.None);
    var verified = await service.InspectAsync(instance.Id);
    if (verified.VerificationError is not null) throw new Exception(verified.VerificationError);
    var installed = verified.Installation ?? throw new Exception("No installation receipt.");
    if (installed.PresetId != selected.Id) throw new Exception("Preset identity missing.");
    if (installed.Hashes.Count != 3) throw new Exception("Expected three BetterRTX materials.");
    foreach (var name in RtxCatalog.MaterialNames)
        if (!File.ReadAllBytes(Path.Combine(materials, name + ".material.bin")).SequenceEqual(original)) throw new Exception("Stock material changed.");
    Console.WriteLine($"Installed {selected.Name}: {installed.Hashes.Count} materials; originals intact.");
    await service.RestoreAsync(instance.Id);
    if ((await service.InspectAsync(instance.Id)).Installation is not null || Directory.GetDirectories(materials, "orion-rtx-*").Length != 0) throw new Exception("Restore incomplete.");
    Console.WriteLine("Original shader paths restored; managed shaders removed.");
    var prizma = presets.Single(p => p.Id == "9825590f-7f4e-4592-b000-8174843da724");
    Console.WriteLine($"Prizma target: {prizma.GameVersion ?? "unknown"}; inferred from material paths: {prizma.VersionFromAssetPath}.");
    if (prizma.GameVersion is not null)
    {
        instance = instance with { Version = prizma.GameVersion.TrimStart('v') };
        await new InstanceRepository(paths).SaveAsync(instance);
        await service.InstallAsync(instance, prizma, false, null, CancellationToken.None);
        if ((await service.InspectAsync(instance.Id)).VerificationError is { } error) throw new Exception(error);
        await new InstanceRepository(paths).SaveAsync(instance with { Version = "99.0.0" });
        var blocked = false;
        try { service.ValidateForLaunchUnderLease(instance.Id); }
        catch (InvalidOperationException) { blocked = true; }
        if (!blocked) throw new Exception("Newer instance bypassed the shader launch policy.");
        await service.RestoreAsync(instance.Id);
        await new InstanceRepository(paths).SaveAsync(instance);
        Console.WriteLine("PASS: current Prizma binaries installed/restored; newer-game launch blocked. This is not an in-game rendering test.");
    }
    var packs = await catalog.TexturesAsync(CancellationToken.None);
    if (packs.Count != 3) throw new Exception("Expected the three Vanilla RTX variants.");
    foreach (var pack in packs)
    {
        await service.InstallTextureAsync(instance.Id, pack, null, CancellationToken.None);
        var detected = await service.InstalledTexturesAsync(instance.Id);
        if (pack.PackId is null || !detected.Any(e => e.PackId == pack.PackId)) throw new Exception("Installed texture identity was not recognized: " + pack.Name);
        Console.WriteLine("Texture imported into isolated user storage: " + pack.Name);
    }
    var snapshot = await content.ListAsync(instance.Id);
    if (snapshot.Entries.Count(e => e.Kind == ContentKind.Texture) != 3) throw new Exception("Texture imports missing.");
    Console.WriteLine("PASS: live catalog, shader downloads, verified installation, restore and three recognized texture imports.");
}
finally
{
    // This program created this exact random test root; it never contains user game data.
    Directory.Delete(root, true);
}

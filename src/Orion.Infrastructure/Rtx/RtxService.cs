using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orion.Application;
using Orion.Domain;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Rtx;

/// <summary>Instance-scoped RTX changes. Original material binaries are never overwritten.</summary>
public sealed partial class RtxService(AppPaths paths, InstanceActivity activity, IRtxCatalog catalog, InstanceContentService content)
{
    public static bool IsVanillaTexture(string? packId) => RtxFamilyPolicy.IsVanilla(packId);
    private string Root(Guid id) => ContentFiles.Safe(paths.Instance(id));
    private static string Materials(string root)
    {
        var game = ContentFiles.Safe(root, "game");
        var executables = ContentFiles.Walk(game, CancellationToken.None).Where(p => Path.GetFileName(p) is "Minecraft.Windows.exe" or "Minecraft.Windows.Preview.exe").Take(2).ToArray();
        if (executables.Length != 1) throw new InvalidDataException("A single installed Minecraft executable is required.");
        var result = ContentFiles.Safe(root, Path.GetRelativePath(root, Path.Combine(Path.GetDirectoryName(executables[0])!, "data/renderer/materials")));
        if (!File.Exists(ContentFiles.Safe(result, "materials.index.json"))) throw new InvalidDataException("This game build has no supported RTX materials index.");
        return result;
    }
    private static T? Read<T>(string root, string relative) => File.Exists(ContentFiles.Safe(root, relative))
        ? JsonSerializer.Deserialize<T>(ContentFiles.ReadSmall(Path.Combine(root, relative))) : default;

    private static GameInstance ReadInstance(string root)
    {
        var instance = JsonSerializer.Deserialize<GameInstance>(ContentFiles.ReadSmall(ContentFiles.Safe(root, "instance.json")), AtomicFile.Json)
            ?? throw new InvalidDataException("Instance metadata is missing.");
        if (!Guid.TryParseExact(Path.GetFileName(root), "N", out var id) || instance.Id != id)
            throw new InvalidDataException("Instance identity mismatch.");
        return instance;
    }

    public Task<RtxInstanceState> InspectAsync(Guid id, CancellationToken ct = default, RtxFamily family = RtxFamily.BetterRtx) => Task.Run(() =>
    {
        using var lease = activity.Acquire(id); var root = Root(id); RecoverUnderLease(id);
        var state = InspectState(root);
        var other = family == RtxFamily.BetterRtx ? RtxFamilyPolicy.VanillaInUse(root, content.ListUnderLease(id)) : RtxFamilyPolicy.BetterInUse(root);
        return state with { Configuration = RtxFamilyPolicy.Configuration(root, family), FamilyConflict = other ? RtxFamilyPolicy.Conflict : null,
            CompatibilityError = InstalledCompatibilityError(root, state.Installation) };
    }, ct);

    public async Task<IReadOnlyList<ContentEntry>> InstalledTexturesAsync(Guid id, CancellationToken ct = default)
        => (await content.ListAsync(id, ct)).Entries.Where(e => e.Kind == ContentKind.Texture && !e.Archived).ToArray();

    public void RecoverUnderLease(Guid id)
    {
        var root = Root(id); RtxTransaction.Recover(root);
        FinishShaderCleanup(root);
        var work = ContentFiles.Safe(root, "rtx");
        if (Directory.Exists(work))
            foreach (var directory in Directory.EnumerateDirectories(work, "download-*"))
                if (Guid.TryParseExact(Path.GetFileName(directory)[9..], "N", out _)) ContentFiles.DeleteWork(directory);
    }

    private static Dictionary<string, byte[]> FamilyConfigurations(string root) => new()
    {
        [RtxFamilyPolicy.ConfigPath(RtxFamily.BetterRtx)] = JsonSerializer.SerializeToUtf8Bytes(RtxFamilyPolicy.Configuration(root, RtxFamily.BetterRtx)),
        [RtxFamilyPolicy.ConfigPath(RtxFamily.VanillaRtx)] = JsonSerializer.SerializeToUtf8Bytes(RtxFamilyPolicy.Configuration(root, RtxFamily.VanillaRtx)),
        ["rtx/configuration.json"] = "null"u8.ToArray()
    };

    private void RequireFamily(Guid id, RtxFamily family)
    {
        var root = Root(id);
        if (family == RtxFamily.BetterRtx ? RtxFamilyPolicy.VanillaInUse(root, content.ListUnderLease(id)) : RtxFamilyPolicy.BetterInUse(root))
            throw new InvalidOperationException(RtxFamilyPolicy.Conflict);
    }

    private void RequireExclusive(Guid id)
    {
        var root = Root(id);
        if (RtxFamilyPolicy.BetterInUse(root) && RtxFamilyPolicy.VanillaInUse(root, content.ListUnderLease(id)))
            throw new InvalidOperationException(RtxFamilyPolicy.Conflict);
    }

    public Task ConfigureAsync(Guid id, RtxConfiguration configuration, CancellationToken ct = default, RtxFamily family = RtxFamily.BetterRtx) => Task.Run(() =>
    {
        using var lease = activity.Acquire(id); var root = Root(id); RecoverUnderLease(id);
        if (!File.Exists(ContentFiles.Safe(root, "instance.json"))) throw new DirectoryNotFoundException("Instance not found.");
        if (RtxFamilyPolicy.Active(configuration)) RequireFamily(id, family);
        if (family == RtxFamily.BetterRtx && RtxFamilyPolicy.Active(configuration)) RequireInstalledCompatibility(root);
        var writes = FamilyConfigurations(root);
        writes[RtxFamilyPolicy.ConfigPath(family)] = JsonSerializer.SerializeToUtf8Bytes(configuration);
        RtxTransaction.Apply(root, writes, ct);
    }, ct);

    public async Task InstallAsync(GameInstance instance, RtxPreset preset, bool acceptMismatch, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        using var lease = activity.Acquire(instance.Id);
        var root = Root(instance.Id); RecoverUnderLease(instance.Id);
        RequireFamily(instance.Id, RtxFamily.BetterRtx);
        if (acceptMismatch && ReadInstance(root).Version != instance.Version)
            throw new InvalidOperationException("The instance version changed. Review the BetterRTX risk warning again.");
        RequireCompatibility(preset.GameVersion, ReadInstance(root).Version, acceptMismatch);
        var work = ContentFiles.Safe(root, "rtx/download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        try
        {
            await catalog.DownloadPresetAsync(preset, work, progress, ct);
            await Task.Run(() => ApplyPreset(root, instance, preset.Name, preset.GameVersion, work, ct, preset.Id, acceptMismatch), ct);
        }
        finally { if (Directory.Exists(work)) ContentFiles.DeleteWork(work); }
    }
    public static void RequireCompatibility(string? target, string game, bool acceptMismatch)
    {
        var compatibility = RtxCatalog.Compatibility(target, game);
        if (compatibility != RtxCompatibility.Matching && !(compatibility == RtxCompatibility.NewerGame && acceptMismatch))
            throw new InvalidOperationException($"BetterRTX blocked: preset target {target ?? "unknown"}, instance {game}. Newer game versions require explicit risk acknowledgement for this preset and game version. Older or unknown targets remain blocked. Restore original Minecraft shaders to play.");
    }

    private static string? InstalledCompatibilityError(string root, RtxInstallation? installation)
    {
        if (installation is null) return null;
        var game = ReadInstance(root);
        try { RequireCompatibility(installation.TargetVersion, game.Version,
            !installation.Recovered && installation.AcceptedNewerGameVersion == game.Version && installation.GameVersion == game.Version); return null; }
        catch (InvalidOperationException ex) { return ex.Message; }
    }
    private static void RequireInstalledCompatibility(string root)
    {
        if (InstalledCompatibilityError(root, InspectState(root).Installation) is { } error) throw new InvalidOperationException(error);
    }

    public Task ImportAsync(GameInstance instance, string archive, bool acceptMismatch, CancellationToken ct) => Task.Run(() =>
    {
        using var lease = activity.Acquire(instance.Id); var root = Root(instance.Id); RecoverUnderLease(instance.Id);
        if (!Path.GetExtension(archive).Equals(".rtpack", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose a .rtpack preset.");
        var work = ContentFiles.Safe(root, "rtx/download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            if (zip.Entries.Count > 256 || zip.Entries.Sum(e => e.Length) > 256L * 1024 * 1024) throw new InvalidDataException("RTX archive exceeds its safety limits.");
            // Reuse the shared traversal/symlink/collision-safe extractor, then copy only known shader slots.
            ContentFiles.Extract(archive, Path.Combine(work, "source"), new(256L * 1024 * 1024, 256), ct);
            var files = ContentFiles.Walk(Path.Combine(work, "source"), ct).ToArray();
            string? targetVersion = null;
            var metadata = files.Where(f => Path.GetFileName(f) == "orion-preset.json").ToArray();
            if (metadata.Length > 1) throw new InvalidDataException("Duplicate preset metadata.");
            if (metadata.Length == 1)
            {
                using var info = JsonDocument.Parse(ContentFiles.ReadSmall(metadata[0]));
                if (info.RootElement.TryGetProperty("gameVersion", out var v) && v.ValueKind == JsonValueKind.String) targetVersion = v.GetString();
            }
            foreach (var name in RtxCatalog.MaterialNames)
            {
                var matches = files.Where(f => Path.GetFileName(f).Equals(name + ".material.bin", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length > 1) throw new InvalidDataException("Duplicate material in preset.");
                if (matches.Length == 1) File.Copy(matches[0], Path.Combine(work, name + ".material.bin"));
            }
            ApplyPreset(root, instance, Path.GetFileNameWithoutExtension(archive), targetVersion, work, ct);
        }
        finally { if (Directory.Exists(work)) ContentFiles.DeleteWork(work); }
    }, ct);

    /// <summary>Native equivalent of the BetterRTX Installer's local material-file flow.</summary>
    public Task ImportMaterialsAsync(GameInstance instance, IReadOnlyList<string> files, bool acceptMismatch, CancellationToken ct) => Task.Run(() =>
    {
        using var lease = activity.Acquire(instance.Id); var root = Root(instance.Id); RecoverUnderLease(instance.Id);
        if (files.Count is < 1 or > 4) throw new InvalidDataException("Select up to four supported RTX material files.");
        var work = ContentFiles.Safe(root, "rtx/download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var name = RtxCatalog.MaterialNames.SingleOrDefault(n => string.Equals(n + ".material.bin", Path.GetFileName(file), StringComparison.OrdinalIgnoreCase));
                if (name is null || !names.Add(name)) throw new InvalidDataException("Unknown or duplicate RTX material file.");
                ContentFiles.Safe(Path.GetDirectoryName(Path.GetFullPath(file))!, Path.GetFileName(file));
                using var source = File.OpenRead(file);
                if (source.Length is < 16 or > 64 * 1024 * 1024) throw new InvalidDataException("Invalid material size.");
                var bytes = new byte[(int)source.Length]; source.ReadExactly(bytes); RtxCatalog.ValidateMaterial(bytes);
                File.WriteAllBytes(Path.Combine(work, name + ".material.bin"), bytes);
            }
            ApplyPreset(root, instance, "BetterRTX · local materials", null, work, ct);
        }
        finally { if (Directory.Exists(work)) ContentFiles.DeleteWork(work); }
    }, ct);

    private static JsonArray Index(string text) => JsonNode.Parse(text, documentOptions: new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })?["materials"] as JsonArray
        ?? throw new InvalidDataException("Unsupported materials index.");
    private static Dictionary<string, JsonObject> Slots(JsonArray entries)
    {
        Dictionary<string, JsonObject> slots = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.OfType<JsonObject>())
        {
            var name = entry["name"]?.GetValue<string>();
            var canonical = RtxCatalog.MaterialNames.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (canonical is not null && !slots.TryAdd(canonical, entry)) throw new InvalidDataException("Duplicate RTX slot in materials index.");
        }
        return slots;
    }
    private static byte[] EncodeIndex(JsonArray entries) => Encoding.UTF8.GetBytes(entries.Root.ToJsonString(new() { WriteIndented = true }));
    private static void ValidateOwned(string root, string materials, RtxInstallation installed, Dictionary<string, JsonObject> slots, bool verifyHashes = true)
    {
        if (!IsOwnedFolder(installed.Folder))
            throw new InvalidDataException("Invalid RTX installation receipt.");
        if (installed.Hashes is null || installed.OriginalPaths is null || installed.Hashes.Count is < 1 or > 4 || installed.OriginalPaths.Count is < 1 or > 4) throw new InvalidDataException("Invalid RTX receipt.");
        foreach (var (name, original) in installed.OriginalPaths)
        {
            if (!RtxCatalog.MaterialNames.Contains(name) || !string.Equals(name, original, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid original RTX path.");
            if (!File.Exists(ContentFiles.Safe(materials, original + ".material.bin")))
                throw new IOException("Original RTX material is missing.");
            var expected = installed.Hashes.ContainsKey(name) ? installed.Folder + "/" + name : original;
            if (!slots.TryGetValue(name, out var slot) || slot["path"]?.GetValue<string>() != expected)
                throw new IOException("RTX files were changed outside Orion. Restore those changes before replacing this preset.");
        }
        foreach (var (name, hash) in installed.Hashes)
        {
            if (!RtxCatalog.MaterialNames.Contains(name) || !installed.OriginalPaths.ContainsKey(name)) throw new InvalidDataException("Invalid RTX material receipt.");
            var path = ContentFiles.Safe(materials, installed.Folder + "/" + name + ".material.bin");
            if (verifyHashes && RtxTransaction.HashFile(path) != hash) throw new IOException("The installed RTX preset has changed or is damaged. Restore the original shaders before reinstalling.");
        }
    }
    private void ApplyPreset(string root, GameInstance instance, string name, string? version, string source, CancellationToken ct, string? presetId = null, bool acceptMismatch = false)
    {
        var reviewedVersion = instance.Version;
        instance = ReadInstance(root);
        if (acceptMismatch && instance.Version != reviewedVersion)
            throw new InvalidOperationException("The instance version changed. Review the BetterRTX risk warning again.");
        RequireCompatibility(version, instance.Version, acceptMismatch);
        RequireFamily(instance.Id, RtxFamily.BetterRtx);
        var materials = Materials(root);
        var indexPath = ContentFiles.Safe(materials, "materials.index.json");
        var entries = Index(ContentFiles.ReadSmall(indexPath, 8 * 1024 * 1024)); var slots = Slots(entries);
        var inspected = InspectState(root);
        if (inspected.VerificationError is not null) throw new IOException(inspected.VerificationError);
        var previous = inspected.Installation;
        if (previous is not null) ValidateOwned(root, materials, previous, slots);
        var folder = "orion-rtx-" + Guid.NewGuid().ToString("N");
        Dictionary<string, byte[]> writes = FamilyConfigurations(root);
        Dictionary<string, string> originals = []; Dictionary<string, string> hashes = [];
        foreach (var material in RtxCatalog.MaterialNames)
        {
            var file = Path.Combine(source, material + ".material.bin");
            if (!slots.TryGetValue(material, out var slot)) { if (File.Exists(file)) throw new InvalidDataException("This game has no slot for " + material); continue; }
            var original = previous?.OriginalPaths.GetValueOrDefault(material) ?? slot["path"]?.GetValue<string>();
            if (original is null || !string.Equals(original, material, StringComparison.OrdinalIgnoreCase)) throw new IOException("A different shader manager already redirects this material.");
            // Verify the stock fallback exists, but never write it.
            var stock = ContentFiles.Safe(materials, original + ".material.bin");
            if (!File.Exists(stock)) throw new IOException("Original RTX material is missing.");
            originals.Add(material, original);
            slot["path"] = original;
            if (!File.Exists(file)) continue;
            if (new FileInfo(file).Length > 64 * 1024 * 1024) throw new InvalidDataException("Oversized material.");
            var bytes = File.ReadAllBytes(file); RtxCatalog.ValidateMaterial(bytes);
            hashes.Add(material, RtxTransaction.Hash(bytes));
            writes.Add(Path.GetRelativePath(root, Path.Combine(materials, folder, material + ".material.bin")), bytes);
            slot["path"] = folder + "/" + material;
        }
        if (hashes.Count == 0) throw new InvalidDataException("The preset contains no supported RTX materials.");
        writes.Add(Path.GetRelativePath(root, indexPath), EncodeIndex(entries));
        writes.Add("rtx/current.json", JsonSerializer.SerializeToUtf8Bytes(new RtxInstallation(name, version, instance.Version, folder, originals, hashes, presetId,
            AcceptedNewerGameVersion: acceptMismatch && RtxCatalog.Compatibility(version, instance.Version) == RtxCompatibility.NewerGame ? instance.Version : null)));
        if (previous is not null) writes.Add("rtx/cleanup.json", JsonSerializer.SerializeToUtf8Bytes(new ShaderCleanup(Path.GetRelativePath(root, materials), previous)));
        RtxTransaction.Apply(root, writes, ct);
        FinishShaderCleanup(root);
    }
    public Task RestoreAsync(Guid id, CancellationToken ct = default) => Task.Run(() =>
    {
        using var lease = activity.Acquire(id); var root = Root(id); RecoverUnderLease(id);
        var state = InspectState(root);
        var previous = state.Installation;
        if (previous is null && state.VerificationError is null) return;
        if (previous is null || !state.CanRestore) throw new IOException(state.VerificationError ?? "RTX cannot be restored safely.");
        var materials = Materials(root); var path = Path.Combine(materials, "materials.index.json");
        var entries = Index(ContentFiles.ReadSmall(path, 8 * 1024 * 1024)); var slots = Slots(entries);
        ValidateOwned(root, materials, previous, slots, verifyHashes: false);
        foreach (var (name, original) in previous.OriginalPaths) slots[name]["path"] = original;
        var writes = FamilyConfigurations(root);
        writes[Path.GetRelativePath(root, path)] = EncodeIndex(entries);
        writes["rtx/current.json"] = "null"u8.ToArray();
        writes["rtx/cleanup.json"] = JsonSerializer.SerializeToUtf8Bytes(new ShaderCleanup(Path.GetRelativePath(root, materials), previous));
        RtxTransaction.Apply(root, writes, ct);
        FinishShaderCleanup(root);
    }, ct);

    public async Task InstallTextureAsync(Guid id, RtxTexturePack pack, IProgress<OperationProgress>? progress, CancellationToken ct,
        Func<string?, string, Task<bool>>? acknowledgeCompatibility = null)
    {
        if (!RtxFamilyPolicy.IsVanilla(pack.PackId)) throw new InvalidDataException("Unknown Vanilla RTX pack identity. Refresh the catalog or update Orion before installing this variant.");
        using (activity.Acquire(id)) { RecoverUnderLease(id); RequireFamily(id, RtxFamily.VanillaRtx); }
        if (pack.PackId is not null && (await InstalledTexturesAsync(id, ct)).Any(e => string.Equals(e.PackId, pack.PackId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This texture is already installed. Manage or archive it in Content before installing another copy.");
        var work = ContentFiles.Safe(paths.Cache, "rtx/pack-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        try
        {
            var archive = Path.Combine(work, "texture.mcpack");
            await catalog.DownloadTextureAsync(pack, archive, progress, ct);
            // Inspect the actual downloaded manifest, not the release/pack version number.
            var minimum = await Task.Run(() =>
            {
                var extracted = ContentArchive.Extract(archive, Path.Combine(work, "manifest-check"), ct);
                if (extracted.Count != 1 || extracted[0].Kind != ContentKind.Texture || !string.Equals(extracted[0].Uuid, pack.PackId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded pack does not match the selected Vanilla RTX identity.");
                return ContentMetadata.Pack(work, extracted[0].Path).MinimumEngineVersion;
            }, ct);
            string gameVersion;
            using (activity.Acquire(id)) { gameVersion = ReadInstance(Root(id)).Version; }
            var comparison = RtxCatalog.Compatibility(minimum, gameVersion);
            if (comparison is RtxCompatibility.OlderGame or RtxCompatibility.Unknown
                && (acknowledgeCompatibility is null || !await acknowledgeCompatibility(minimum, gameVersion)))
                throw new OperationCanceledException("Vanilla RTX installation requires acknowledgement of its minimum game version.", ct);
            ct.ThrowIfCancellationRequested();
            await content.ImportCheckedAsync(id, archive, null, root =>
            {
                if (ReadInstance(root).Version != gameVersion)
                    throw new InvalidOperationException("The instance version changed. Review compatibility again before installing.");
            }, ct);
        }
        finally { if (Directory.Exists(work)) ContentFiles.DeleteWork(work); }
    }

    public bool UsesRtxUnderLease(Guid id)
    {
        var root = Root(id);
        return RtxFamilyPolicy.BetterInUse(root) || RtxFamilyPolicy.VanillaInUse(root, content.ListUnderLease(id));
    }

    public void PrepareLaunchUnderLease(Guid id, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var root = Root(id); RecoverUnderLease(id);
        RequireHealthyShaders(root);
        RequireInstalledCompatibility(root);
        RequireExclusive(id);
        var configuration = RtxFamilyPolicy.Configuration(root, RtxFamilyPolicy.BetterInUse(root) ? RtxFamily.BetterRtx : RtxFamily.VanillaRtx);
        if (!configuration.EnableOnLaunch && !configuration.AdvancedVideo) return;
        var users = ContentFiles.Safe(root, "prefix/drive_c/users");
        Dictionary<string, byte[]> writes = [];
        if (Directory.Exists(users))
            foreach (var user in Directory.EnumerateDirectories(users))
            foreach (var edition in new[] { "Minecraft Bedrock", "Minecraft Bedrock Preview" })
            {
                var storage = ContentFiles.Safe(root, Path.GetRelativePath(root, Path.Combine(user, "AppData/Roaming", edition, "Users")));
                if (!Directory.Exists(storage)) continue;
                foreach (var profile in Directory.EnumerateDirectories(storage))
                {
                    if (Path.GetFileName(profile) == "Shared") continue;
                    var relative = Path.GetRelativePath(root, Path.Combine(profile, "games/com.mojang/minecraftpe/options.txt"));
                    var path = ContentFiles.Safe(root, relative); if (!File.Exists(path)) continue;
                    writes.Add(relative, Encoding.UTF8.GetBytes(LaunchOptions(ContentFiles.ReadSmall(path), configuration)));
                }
            }
        if (writes.Count > 0) RtxTransaction.Apply(root, writes, ct);
        progress?.Report(new(writes.Count > 0 ? "Applied RTX launch preferences" : "RTX: start and sign into the game once to create its graphics preferences"));
    }
    public static string LaunchOptions(string source, RtxConfiguration configuration)
    {
        if (!configuration.EnableOnLaunch && !configuration.AdvancedVideo) return source;
        Dictionary<string, string> options = [];
        if (configuration.EnableOnLaunch) { options.Add("graphics_mode", "3"); options.Add("graphics_mode_switch", "1"); }
        if (configuration.EnableOnLaunch && configuration.DisableVSync) options.Add("gfx_vsync", "0");
        if (configuration.AdvancedVideo) options.Add("show_advanced_video_settings", "1");
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = source.Replace("\r\n", "\n").Split('\n').Where(l => !options.Keys.Any(k => l.StartsWith(k + ":", StringComparison.Ordinal))).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.AddRange(options.Select(p => p.Key + ":" + p.Value));
        return string.Join(newline, lines) + newline;
    }
}

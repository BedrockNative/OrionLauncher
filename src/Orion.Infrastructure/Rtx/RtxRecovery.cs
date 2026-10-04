using System.Text.Json;
using Orion.Domain;
using Orion.Infrastructure.Content;

namespace Orion.Infrastructure.Rtx;

public sealed partial class RtxService
{
    private sealed record ShaderCleanup(string Materials, RtxInstallation Installation);
    private static bool IsOwnedFolder(string? folder) => folder is not null && folder.StartsWith("orion-rtx-", StringComparison.Ordinal)
        && Guid.TryParseExact(folder[10..], "N", out _);

    private static RtxInstanceState InspectState(string root)
    {
        var configuration = Read<RtxConfiguration>(root, "rtx/configuration.json") ?? new();
        RtxInstallation? installed = null;
        string? receiptError = null;
        try { installed = Read<RtxInstallation>(root, "rtx/current.json"); }
        catch (JsonException) { receiptError = "The installation receipt is damaged."; }
        try
        {
            var materials = Materials(root);
            var slots = Slots(Index(ContentFiles.ReadSmall(Path.Combine(materials, "materials.index.json"), 8 * 1024 * 1024)));
            if (installed is null)
            {
                var redirected = slots.Where(s => !string.Equals(s.Value["path"]?.GetValue<string>(), s.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (redirected.Length == 0) return new(configuration, null, receiptError);
                // A missing receipt is not proof of a vanilla installation. Recover only Orion's
                // unambiguous namespace; never take ownership of another shader manager's paths.
                var folders = redirected.Select(s => s.Value["path"]?.GetValue<string>()?.Split('/')[0]).Distinct().ToArray();
                if (folders.Length != 1 || !IsOwnedFolder(folders[0])) return new(configuration, null, "External RTX redirects detected. Restore them with their original shader manager.");
                var original = new Dictionary<string, string>(); var hashes = new Dictionary<string, string>();
                foreach (var (name, slot) in slots)
                {
                    var path = slot["path"]?.GetValue<string>();
                    if (path == folders[0] + "/" + name) { original.Add(name, name); hashes.Add(name, "unverified"); }
                    else if (!string.Equals(path, name, StringComparison.OrdinalIgnoreCase))
                        return new(configuration, null, "RTX redirects cannot be recovered unambiguously.");
                }
                installed = new("Recovered Orion preset", null, "", folders[0]!, original, hashes, Recovered: true);
            }
            ValidateOwned(root, materials, installed, slots, verifyHashes: false);
            try
            {
                if (installed.Recovered) throw new IOException("RTX files were found without a valid receipt. Restore originals before reinstalling; existing files will be preserved in quarantine.");
                ValidateOwned(root, materials, installed, slots);
                return new(configuration, installed, null, true);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { return new(configuration, installed, ex.Message, true); }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException)
        { return new(configuration, installed, ex.Message); }
    }

    public void ValidateForLaunchUnderLease(Guid id)
    {
        var root = Root(id); RecoverUnderLease(id); RequireHealthyShaders(root);
        RequireInstalledCompatibility(root);
        RequireExclusive(id);
    }
    private static void RequireHealthyShaders(string root)
    {
        // Non-RTX game builds remain launchable when no Orion receipt exists.
        try { _ = Materials(root); }
        catch (InvalidDataException) when (!File.Exists(ContentFiles.Safe(root, "rtx/current.json"))) { return; }
        var state = InspectState(root);
        if (state.VerificationError is not null) throw new IOException("RTX needs attention before launch: " + state.VerificationError);
    }

    private static void FinishShaderCleanup(string root)
    {
        string materials;
        try { materials = Materials(root); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
        var entries = Index(ContentFiles.ReadSmall(Path.Combine(materials, "materials.index.json"), 8 * 1024 * 1024));
        var referenced = entries.OfType<System.Text.Json.Nodes.JsonObject>().Select(e => e["path"]?.GetValue<string>()?.Split('/')[0]).ToHashSet();
        var pending = Read<ShaderCleanup>(root, "rtx/cleanup.json");
        if (pending is not null)
        {
            if (ContentFiles.Safe(root, pending.Materials) != materials || !IsOwnedFolder(pending.Installation.Folder))
                throw new InvalidDataException("Invalid shader cleanup record.");
            var path = ContentFiles.Safe(materials, pending.Installation.Folder);
            if (referenced.Contains(pending.Installation.Folder)) throw new IOException("Cannot clean shaders still referenced by the game.");
            if (Directory.Exists(path))
            {
                var expected = pending.Installation.Hashes;
                var safe = !pending.Installation.Recovered && expected is not null && expected.Count > 0
                    && Directory.EnumerateFileSystemEntries(path).Count() == expected.Count
                    && expected.All(p => RtxCatalog.MaterialNames.Contains(p.Key)
                        && RtxTransaction.HashFile(ContentFiles.Safe(path, p.Key + ".material.bin")) == p.Value);
                if (safe) ContentFiles.DeleteWork(path); else Quarantine(root, path);
            }
            File.Delete(ContentFiles.Safe(root, "rtx/cleanup.json"));
        }
        // Upgrade recovery: older builds did not journal post-commit cleanup. Preserve unknown
        // orphan bytes rather than guessing whether they are safe to delete.
        foreach (var folder in Directory.EnumerateDirectories(materials, "orion-rtx-*"))
            if (IsOwnedFolder(Path.GetFileName(folder)) && !referenced.Contains(Path.GetFileName(folder)))
                Quarantine(root, ContentFiles.Safe(materials, Path.GetFileName(folder)));
    }
    private static void Quarantine(string root, string folder)
    {
        var destination = ContentFiles.Safe(root, "rtx/quarantine/" + Path.GetFileName(folder));
        if (Directory.Exists(destination)) throw new IOException("A shader quarantine with this name already exists; preserve it before retrying recovery.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(folder, destination);
    }
}

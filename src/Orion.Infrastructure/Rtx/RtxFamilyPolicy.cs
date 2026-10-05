using System.Text.Json;
using Orion.Domain;
using Orion.Infrastructure.Content;

namespace Orion.Infrastructure.Rtx;

/// <summary>Called under the instance lease, including non-Studio content imports.</summary>
internal static class RtxFamilyPolicy
{
    internal const string Conflict = "BetterRTX and Vanilla RTX are exclusive in this instance. Restore BetterRTX shaders and reset its preferences, or archive/unlink Vanilla RTX packs, restore any replaced DLSS DLL and reset Vanilla RTX preferences. No content was removed.";
    internal static bool IsVanilla(string? uuid) => uuid is not null && new[]
    {
        "7c87f859-4d79-4d51-8887-bf450b2b2bfa", "bbe2b225-b45b-41c2-bd3b-465cd83e6071", "a5c3cc7d-1740-4b5e-ae2c-71bc14b3f63b"
    }.Contains(uuid, StringComparer.OrdinalIgnoreCase);
    internal static string ConfigPath(RtxFamily family) => family switch
    {
        RtxFamily.BetterRtx => "rtx/betterrtx/configuration.json",
        RtxFamily.VanillaRtx => "rtx/vanillartx/configuration.json",
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };
    private static T? Read<T>(string root, string relative)
    {
        var path = ContentFiles.Safe(root, relative);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(ContentFiles.ReadSmall(path)) : default;
    }
    internal static bool HasShaders(string root, IEnumerable<string>? materialIndexes = null)
    {
        try { if (Read<RtxInstallation>(root, "rtx/current.json") is not null) return true; }
        catch (JsonException) { /* The index remains authoritative for recovery and legacy ownership. */ }
        // Detect missing receipts and external redirects too. Never follow shader symlinks.
        var game = ContentFiles.Safe(root, "game");
        if (!Directory.Exists(game)) return false;
        // Recovery may have quarantined an orphan since the per-launch scan.
        var indexes = materialIndexes?.Where(File.Exists)
            ?? ContentFiles.Walk(game, CancellationToken.None).Where(p => Path.GetFileName(p) == "materials.index.json");
        foreach (var file in indexes)
        {
            using var json = JsonDocument.Parse(ContentFiles.ReadSmall(file, 8 * 1024 * 1024), new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!json.RootElement.TryGetProperty("materials", out var slots)) continue;
            foreach (var slot in slots.EnumerateArray())
                if (slot.TryGetProperty("name", out var n) && RtxCatalog.MaterialNames.Contains(n.GetString(), StringComparer.OrdinalIgnoreCase)
                    && slot.TryGetProperty("path", out var p) && !string.Equals(n.GetString(), p.GetString(), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    internal static RtxConfiguration Configuration(string root, RtxFamily family, bool? hasShaders = null)
    {
        var current = Read<RtxConfiguration>(root, ConfigPath(family));
        if (current is not null) return current;
        // Legacy settings belong to the existing shader provider; otherwise to stock RTX.
        var legacy = Read<RtxConfiguration>(root, "rtx/configuration.json");
        return legacy is not null && family == ((hasShaders ?? HasShaders(root)) ? RtxFamily.BetterRtx : RtxFamily.VanillaRtx) ? legacy : new();
    }
    internal static bool Active(RtxConfiguration config) => config.EnableOnLaunch || config.DisableVSync || config.AdvancedVideo;
    internal static bool BetterInUse(string root, bool? hasShaders = null) => (hasShaders ?? HasShaders(root)) || Active(Configuration(root, RtxFamily.BetterRtx, hasShaders));
    internal static bool VanillaInUse(string root, IEnumerable<ContentEntry> entries, bool? hasShaders = null) => entries.Any(e => !e.Archived
        && (IsVanilla(e.PackId) || e.Kind == ContentKind.World && WorldHasVanilla(ContentFiles.Safe(root, e.Id))))
        || Read<DlssInstallation>(root, "rtx/dlss/current.json") is not null || Active(Configuration(root, RtxFamily.VanillaRtx, hasShaders));
    private static bool WorldHasVanilla(string world)
    {
        var packs = ContentFiles.Safe(world, "resource_packs");
        if (!Directory.Exists(packs)) return false;
        foreach (var pack in Directory.EnumerateDirectories(packs))
        {
            var manifest = ContentFiles.Safe(pack, "manifest.json");
            if (!File.Exists(manifest)) continue;
            using var json = JsonDocument.Parse(ContentFiles.ReadSmall(manifest), new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (json.RootElement.TryGetProperty("header", out var header) && header.TryGetProperty("uuid", out var uuid) && IsVanilla(uuid.GetString())) return true;
        }
        return false;
    }
    internal static void RequireWorldImport(string root, string world)
    {
        if (WorldHasVanilla(world)) RequireVanillaImport(root, ["7c87f859-4d79-4d51-8887-bf450b2b2bfa"]);
    }
    internal static void RequireVanillaImport(string root, IEnumerable<string?> ids)
    {
        if (!ids.Any(IsVanilla)) return;
        RtxTransaction.Recover(root);
        if (BetterInUse(root)) throw new InvalidOperationException(Conflict);
    }
}

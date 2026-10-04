using System.IO.Compression;
using System.Text.Json;
using Orion.Infrastructure.Content;

namespace Orion.Infrastructure.Rtx;

public sealed partial class RtxService
{
    public Task ExportAsync(Guid id, string destination, bool original, CancellationToken ct = default) => Task.Run(() =>
    {
        using var lease = activity.Acquire(id); var root = Root(id); RecoverUnderLease(id);
        var materials = Materials(root); var state = InspectState(root);
        if (!original && state.VerificationError is not null) throw new IOException(state.VerificationError);
        var full = Path.GetFullPath(destination);
        ContentFiles.Safe(Path.GetDirectoryName(full)!, Path.GetFileName(full));
        if (!full.EndsWith(".rtpack", StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + "/", StringComparison.Ordinal))
            throw new InvalidDataException("Export to a .rtpack file outside this instance.");
        // Stage externally, publish without overwrite only after the archive is complete.
        var stage = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(stage, FileMode.CreateNew))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var slots = Slots(Index(ContentFiles.ReadSmall(Path.Combine(materials, "materials.index.json"), 8 * 1024 * 1024)));
                foreach (var (name, slot) in slots)
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = original ? state.Installation?.OriginalPaths.GetValueOrDefault(name) ?? name : slot["path"]!.GetValue<string>();
                    var file = ContentFiles.Safe(materials, relative + ".material.bin");
                    using var input = File.OpenRead(file);
                    if (input.Length is < 16 or > 64 * 1024 * 1024) throw new InvalidDataException("Invalid shader size.");
                    using var output = zip.CreateEntry(name + ".material.bin", CompressionLevel.Fastest).Open();
                    ContentFiles.Copy(input, output, new(64 * 1024 * 1024, 1), ct);
                }
                using var metadata = new StreamWriter(zip.CreateEntry("orion-preset.json").Open());
                metadata.Write(JsonSerializer.Serialize(new { name = original ? "Original Minecraft shaders" : state.Installation?.Name, source = "Orion", original,
                    gameVersion = original || state.Installation is null ? ReadInstance(root).Version : state.Installation.TargetVersion }));
            }
            ct.ThrowIfCancellationRequested(); File.Move(stage, full, false);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }, ct);
}

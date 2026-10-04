using Orion.Domain;

namespace Orion.Infrastructure.Content;

internal static class ContentArchive
{
    internal sealed record Part(string Path, ContentKind Kind, string? Uuid);
    internal static IReadOnlyList<Part> Extract(string file, string work, CancellationToken ct)
    {
        var extension = Path.GetExtension(file).ToLowerInvariant();
        if (extension is not (".mcworld" or ".mcpack" or ".mcaddon")) throw new InvalidDataException("Choose a .mcworld, .mcpack or .mcaddon file.");
        var extracted = Path.Combine(work, "extracted");
        var budget = new ContentFiles.Budget(); ContentFiles.Extract(file, extracted, budget, ct);
        if (extension == ".mcworld")
        {
            var worlds = ContentFiles.Walk(extracted, ct).Where(p => Path.GetFileName(p) == "level.dat").Select(p => Path.GetDirectoryName(p)!).ToArray();
            if (worlds.Length != 1 || !Directory.Exists(Path.Combine(worlds[0], "db"))) throw new InvalidDataException("A world must contain level.dat and its db directory.");
            return [new(worlds[0], ContentKind.World, null)];
        }
        if (extension == ".mcaddon")
            foreach (var nested in ContentFiles.Walk(extracted, ct).Where(p => Path.GetExtension(p).Equals(".mcpack", StringComparison.OrdinalIgnoreCase)).ToArray())
                ContentFiles.Extract(nested, Path.Combine(work, "nested", Guid.NewGuid().ToString("N")), budget, ct);
        var parts = ContentFiles.Walk(work, ct).Where(p => Path.GetFileName(p) == "manifest.json").Select(manifest =>
        {
            var folder = Path.GetDirectoryName(manifest)!; var pack = ContentMetadata.Pack(work, folder);
            return new Part(folder, pack.Kind, pack.PackId);
        }).ToArray();
        if (parts.Length == 0 || extension == ".mcpack" && parts.Length != 1 || parts.Select(p => p.Uuid).Distinct().Count() != parts.Length)
            throw new InvalidDataException("The archive contains missing or duplicate pack manifests.");
        if (parts.Any(a => parts.Any(b => a.Path != b.Path && a.Path.StartsWith(b.Path + "/", StringComparison.Ordinal))))
            throw new InvalidDataException("Nested pack manifests are not supported.");
        return parts;
    }
}

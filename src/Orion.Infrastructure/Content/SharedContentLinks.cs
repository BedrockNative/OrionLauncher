using Orion.Domain;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Content;

/// <summary>The only allowed content link: an Orion-named leaf targeting a validated library pack.</summary>
internal static class SharedContentLinks
{
    internal static string Library(AppPaths paths) => ContentFiles.Safe(paths.Data, "content-library");
    internal static string Item(AppPaths paths, Guid id) => ContentFiles.Safe(Library(paths), $"items/{id:N}");
    internal static string Name(Guid item, string uuid) => $"orion-shared-{item:N}-{Guid.Parse(uuid):N}";
    internal static string Resolve(AppPaths paths, string path)
    {
        var parent = ContentFiles.Safe(Path.GetDirectoryName(path)!);
        var link = new DirectoryInfo(path).LinkTarget;
        if (link is null) return ContentFiles.Safe(parent, Path.GetFileName(path));
        var name = Path.GetFileName(path);
        const string prefix = "orion-shared-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length != prefix.Length + 65
            || !Guid.TryParseExact(name.Substring(prefix.Length, 32), "N", out var item)
            || name[prefix.Length + 32] != '-'
            || !Guid.TryParseExact(name[(prefix.Length + 33)..], "N", out var pack))
            throw new InvalidDataException("Unrecognized symbolic link in game content.");
        var expected = ContentFiles.Safe(Item(paths, item), $"payload/{pack:N}");
        if (!Path.IsPathFullyQualified(link) || link != expected) throw new InvalidDataException("Shared pack link points outside its library entry.");
        var metadata = ContentMetadata.Pack(expected, expected);
        if (metadata.PackId != pack.ToString() || Path.GetFileName(parent) != (metadata.Kind == ContentKind.Addon ? "behavior_packs" : "resource_packs"))
            throw new InvalidDataException("Shared content type or identity mismatch.");
        return expected;
    }
}

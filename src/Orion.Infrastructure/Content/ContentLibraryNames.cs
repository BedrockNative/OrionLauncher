using Orion.Domain;

namespace Orion.Infrastructure.Content;

internal static class ContentLibraryNames
{
    public static string Resolve(string? name, IReadOnlyList<ContentEntry> entries)
    {
        if (!string.IsNullOrWhiteSpace(name) && !name.Equals("download", StringComparison.OrdinalIgnoreCase)) return name;
        var primary = entries.FirstOrDefault(e => e.Kind == ContentKind.Addon) ?? entries[0];
        // Collapse a matching BP/RP pair, but never guess a common prefix for
        // unrelated packs bundled in the same archive.
        var names = entries.Select(e => WithoutPackSuffix(e.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return names.Length == 1 ? names[0] : primary.Name;
    }

    private static string WithoutPackSuffix(string name)
    {
        foreach (var suffix in new[] { " BP", " RP", " (BP)", " (RP)", " [BP]", " [RP]" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
                return name[..^suffix.Length].TrimEnd();
        return name;
    }
}

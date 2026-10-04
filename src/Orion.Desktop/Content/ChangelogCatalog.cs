using System.Text.RegularExpressions;
using Orion.Desktop.I18n;

namespace Orion.Desktop.Content;

public sealed record ChangelogEntry(string Title, string Content, bool IsLegacy)
{
    public override string ToString() => Title;
}

public sealed record ChangelogBlock(string Text, bool IsHeading, bool IsBullet);

/// <summary>Bundled release notes work offline and in single-file distributions.</summary>
public static partial class ChangelogCatalog
{
    public static IReadOnlyList<ChangelogEntry> Read(Localizer text, string language)
    {
        var assembly = typeof(ChangelogCatalog).Assembly;
        string ReadResource(string name)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidDataException($"Missing bundled changelog: {name}");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        var resources = assembly.GetManifestResourceNames().ToHashSet(StringComparer.Ordinal);
        List<ChangelogEntry> entries = [];
        const string releasePrefix = "Changelog.release/en_US/v";
        var releaseLocale = language == "pt-BR" ? "pt_BR" : "en_US";
        foreach (var resource in resources.Where(n => n.StartsWith(releasePrefix, StringComparison.Ordinal))
            .OrderByDescending(n => Version.Parse(n[releasePrefix.Length..^3])))
        {
            var version = resource[releasePrefix.Length..^3];
            var localized = $"Changelog.release/{releaseLocale}/v{version}.md";
            entries.Add(new($"{version} · {text["CurrentChangelog"]}",
                ReadResource(resources.Contains(localized) ? localized : resource), false));
        }
        var locale = language == "pt-BR" ? "PT-BR" : "EN-US";
        var prefix = $"Changelog.legacy/{locale}/v";
        foreach (var resource in resources.Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(n => Version.Parse(n[prefix.Length..^3])))
        {
            var version = resource[prefix.Length..^3];
            entries.Add(new($"{version} · {text["LegacyChangelog"]}", ReadResource(resource), true));
        }
        entries.Add(new(text["FullLegacyChangelog"], ReadResource("Changelog.legacy/changelog.md"), true));
        return entries;
    }

    // Deliberately limited to the bundled notes' headings, lists and paragraphs.
    // No HTML, embedded images, remote content or executable links are evaluated.
    public static IReadOnlyList<ChangelogBlock> Format(string markdown)
    {
        List<ChangelogBlock> blocks = [];
        string? paragraph = null;
        bool bullet = false;
        void Flush()
        {
            if (paragraph is null) return;
            blocks.Add(new(InlineText(paragraph), false, bullet));
            paragraph = null; bullet = false;
        }
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line == "---") { Flush(); continue; }
            if (line.StartsWith('#')) { Flush(); blocks.Add(new(InlineText(line.TrimStart('#', ' ')), true, false)); }
            else if (line.StartsWith("- ") || line.StartsWith("* ")) { Flush(); paragraph = line[2..]; bullet = true; }
            else paragraph = paragraph is null ? line : $"{paragraph} {line}";
        }
        Flush();
        return blocks;
    }
    private static string InlineText(string value) =>
        MarkdownLink().Replace(value, "$1 ($2)").Replace("**", "").Replace("`", "");

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex MarkdownLink();
}

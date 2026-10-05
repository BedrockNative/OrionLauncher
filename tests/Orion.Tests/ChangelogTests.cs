using Orion.Desktop.Content;
using Orion.Desktop.I18n;

namespace Orion.Tests;

public sealed class ChangelogTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public void BundledNotesIncludeCurrentReleaseLocalizedVersionsAndFullArchive(string language)
    {
        var text = new Localizer();
        text.SetLanguage(language);
        var entries = ChangelogCatalog.Read(text, language);
        var current = typeof(ChangelogCatalog).Assembly.GetName().Version!;
        var releases = entries.Where(entry => !entry.IsLegacy).ToArray();
        var historical = entries.Where(entry => entry.IsLegacy).ToArray();
        Assert.Equal(new[] { "1.0.2", "1.0.1", "1.0.0" }.Where(version => Version.Parse(version) <= current),
            releases.Select(entry => entry.Title.Split(' ')[0]));
        Assert.Equal(6, historical.Length);
        Assert.False(entries[0].IsLegacy);
        Assert.StartsWith(current.ToString(3), entries[0].Title);
        Assert.StartsWith("# Orion Launcher " + current.ToString(3), entries[0].Content);
        var original = entries.Single(entry => entry.Title.StartsWith("1.0.0"));
        Assert.Contains(language == "pt-BR" ? "Logs unificados e ao vivo" : "Unified live instance logs", original.Content);
        Assert.DoesNotContain("0.6.0", entries[0].Content);
        Assert.DoesNotContain("development", entries[0].Title);
        Assert.All(entries.Where(entry => !entry.IsLegacy), entry =>
            Assert.True(Version.Parse(entry.Title.Split(' ')[0]) <= current));
        Assert.DoesNotContain("desenvolvimento", entries[0].Title);
        Assert.StartsWith("0.5.0", historical[0].Title);
        Assert.Contains("Historical release", ChangelogCatalog.Read(new Localizer(), "unknown").First(entry => entry.IsLegacy).Title);
        Assert.All(entries, entry => Assert.NotEmpty(ChangelogCatalog.Format(entry.Content)));
        var catalogNotes = entries.Single(e => e.Title.StartsWith("0.4.2"));
        Assert.Contains(language == "pt-BR" ? "Catálogo" : "catalog", catalogNotes.Content);
        Assert.Contains("## 0.5.0", entries[^1].Content);
        Assert.Contains("## Unreleased", entries[^1].Content);
    }

    [Fact]
    public void UnknownLanguageFallsBackToCompleteEnglishReleaseNotes()
    {
        var text = new Localizer();
        Assert.Equal(ChangelogCatalog.Read(text, "en-US")[0], ChangelogCatalog.Read(text, "unknown")[0]);
    }

    [Fact]
    public void ReleaseTranslationsCoverTheSameSectionsAndHighlights()
    {
        var text = new Localizer();
        var english = ChangelogCatalog.Read(text, "en-US").Single(e => e.Title.StartsWith("1.0.0")).Content;
        var portuguese = ChangelogCatalog.Read(text, "pt-BR").Single(e => e.Title.StartsWith("1.0.0")).Content;
        Assert.NotEqual(english, portuguese);
        var englishBlocks = ChangelogCatalog.Format(english);
        var portugueseBlocks = ChangelogCatalog.Format(portuguese);
        Assert.Equal(englishBlocks.Select(b => (b.IsHeading, b.IsBullet)),
            portugueseBlocks.Select(b => (b.IsHeading, b.IsBullet)));
        foreach (var keyword in new[] { "GDK", "Marketplace", "WineGDK", "KDE", "CurseForge", "RTX Studio",
            ".mcaddon", ".mcworld", "prime-run %command%", "orion.xodus.sock" })
        {
            Assert.Contains(keyword, english);
            Assert.Contains(keyword, portuguese);
        }
        Assert.Contains("native binary modding system has been removed", english);
        Assert.Contains("mods nativos/binários foi removido", portuguese);
    }

    [Fact]
    public void FormatterPreservesWrappedContentAndRendersBasicNotesWithoutMarkdownMarkers()
    {
        var blocks = ChangelogCatalog.Format("# Heading\r\n\r\n- **Bold** and `code`\r\n  continued\r\n\r\nParagraph with [reference](https://example.test).\r\nNext line.\r\n---\r\n");
        Assert.Equal(3, blocks.Count);
        Assert.True(blocks[0].IsHeading);
        Assert.Equal("Heading", blocks[0].Text);
        Assert.True(blocks[1].IsBullet);
        Assert.Equal("Bold and code continued", blocks[1].Text);
        Assert.False(blocks[2].IsBullet);
        Assert.Equal("Paragraph with reference (https://example.test). Next line.", blocks[2].Text);
    }
}

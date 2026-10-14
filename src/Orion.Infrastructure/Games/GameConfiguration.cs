using System.Xml;
using System.Xml.Linq;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Games;

public static class GameConfiguration
{
    public static async Task EnsureAsync(string game, string executable, string channel, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(executable)!;
        var existing = Directory.EnumerateFiles(directory).FirstOrDefault(p => Path.GetFileName(p).Equals("MicrosoftGame.config", StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { ContentFiles.Safe(game, Path.GetRelativePath(game, existing)); return; }
        var manifest = Directory.EnumerateFiles(directory).FirstOrDefault(p => Path.GetFileName(p).Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Cannot generate MicrosoftGame.config: AppxManifest.xml is missing.");
        ContentFiles.Safe(game, Path.GetRelativePath(game, manifest));
        using var reader = XmlReader.Create(manifest, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
        var identity = document.Root?.Elements().SingleOrDefault(e => e.Name.LocalName == "Identity")
            ?? throw new InvalidDataException("The package manifest has no identity.");
        string Required(string name) => identity.Attribute(name)?.Value is { Length: > 0 } value ? value
            : throw new InvalidDataException($"The package identity has no {name}.");
        var preview = channel.Equals("preview", StringComparison.OrdinalIgnoreCase);
        var title = document.Descendants().Where(e => e.Name.LocalName == "Protocol")
            .Select(e => (string?)e.Attribute("Name")).FirstOrDefault(n => n is not null && n.StartsWith("ms-xbl-", StringComparison.OrdinalIgnoreCase));
        var titleId = title?[7..];
        if (titleId is null || titleId.Length != 8 || !titleId.All(Uri.IsHexDigit)) titleId = preview ? "717D695F" : "35760C07";
        var languages = document.Descendants().Where(e => e.Name.LocalName == "Resource")
            .Select(e => (string?)e.Attribute("Language")).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var config = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("Game", new XAttribute("configVersion", "1"),
            new XElement("Identity", new XAttribute("Name", Required("Name")), new XAttribute("Publisher", Required("Publisher")), new XAttribute("Version", Required("Version"))),
            new XElement("TitleId", titleId.ToUpperInvariant()),
            new XElement("MSAAppId", preview ? "00000000403FC600" : "0000000040159362"),
            new XElement("StoreId", preview ? "9P5X4QVLC2XR" : "9NBLGGH2JHXJ"),
            new XElement("ExecutableList", new XElement("Executable", new XAttribute("Name", Path.GetFileName(executable)), new XAttribute("TargetDeviceFamily", "PC"), new XAttribute("Id", "Game"))),
            new XElement("ProtocolList", new XElement("Protocol", new XAttribute("Name", preview ? "minecraft-preview" : "minecraft"))),
            new XElement("Resources", (languages.Length == 0 ? ["en-us"] : languages).Select(l => new XElement("Resource", new XAttribute("Language", l!)))),
            new XElement("AdvancedUserModel", true), new XElement("MSAFullTrust", true)));
        var destination = ContentFiles.Safe(game, Path.GetRelativePath(game, Path.Combine(directory, "MicrosoftGame.config")));
        await AtomicFile.WriteAsync(destination, config.ToString(), ct);
    }
}

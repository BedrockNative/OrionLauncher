using System.Xml.Linq;
using Orion.Infrastructure.Games;

namespace Orion.Tests;

public sealed class GameConfigurationTests
{
    public const string Manifest = """
        <Package xmlns="urn:appx"><Identity Version="1.26.5203.0" Publisher="CN=Microsoft &amp; Co" Name="Microsoft.MinecraftUWP"/><Resources><Resource Language="pt-BR"/></Resources><Applications><Application><Protocol Name="ms-xbl-ABCDEF12"/></Application></Applications></Package>
        """;

    [Theory]
    [InlineData("release", "0000000040159362", "9NBLGGH2JHXJ")]
    [InlineData("preview", "00000000403FC600", "9P5X4QVLC2XR")]
    public async Task GeneratesFromManifestAndPreservesExisting(string channel, string msa, string store)
    {
        using var d = new TestDirectory();
        await File.WriteAllTextAsync(Path.Combine(d.Root, "appxmanifest.xml"), Manifest);
        var exe = Path.Combine(d.Root, "Minecraft.Windows.exe");
        await GameConfiguration.EnsureAsync(d.Root, exe, channel);
        var path = Path.Combine(d.Root, "MicrosoftGame.config");
        var game = XDocument.Load(path).Root!;
        Assert.Equal(msa, game.Element("MSAAppId")!.Value);
        Assert.Equal(store, game.Element("StoreId")!.Value);
        Assert.Equal("ABCDEF12", game.Element("TitleId")!.Value);
        Assert.Equal("CN=Microsoft & Co", (string?)game.Element("Identity")!.Attribute("Publisher"));
        var time = File.GetLastWriteTimeUtc(path);
        await GameConfiguration.EnsureAsync(d.Root, exe, channel);
        Assert.Equal(time, File.GetLastWriteTimeUtc(path));
        File.Delete(path);
        await GameConfiguration.EnsureAsync(d.Root, exe, channel);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task MissingManifestFailsBeforeLaunch()
    {
        using var d = new TestDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => GameConfiguration.EnsureAsync(d.Root, Path.Combine(d.Root, "Minecraft.Windows.exe"), "release"));
    }
}

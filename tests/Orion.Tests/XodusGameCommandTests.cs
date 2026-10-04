using Orion.Domain;
using Orion.Infrastructure.Games;

namespace Orion.Tests;

public sealed class XodusGameCommandTests
{
    [Theory]
    [InlineData(false, "https://example.test/game.msixvc", "https://example.test/game.msixvc")]
    [InlineData(true, "/games/my package.msixvc", "file:///games/my package.msixvc")]
    public void AllInstallationSourcesRequirePurchase(bool local, string source, string expected)
    {
        Assert.Equal(["install-owned", "9NBLGGH2JHXJ", expected, "/instance/game"],
            XodusGameCommands.Install(source, "/instance/game", local));
    }

    [Fact]
    public void PlayUsesOnlyTheInstalledLicense() =>
        Assert.Equal(["run", "/game", "/wine", "--exe", "Minecraft.Windows.exe", "--offline-license"],
            XodusGameCommands.Play("/game", "/wine", "Minecraft.Windows.exe"));

    [Theory]
    [InlineData("0.1.0-389563f")]
    [InlineData("unknown")]
    public void OldOrUnrecognizedRuntimeCannotBypassTheGate(string tag) =>
        Assert.Throws<InvalidOperationException>(() => XodusGameCommands.RequirePurchaseApi(new("xodus", tag, "/runtime")));

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("v0.3.0")]
    public void SupportedRuntimeIsAccepted(string tag) =>
        XodusGameCommands.RequirePurchaseApi(new("xodus", tag, "/runtime"));
}

using Orion.Domain;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Processes;
using Orion.Desktop.ViewModels;

namespace Orion.Tests;

public sealed class LaunchCommandTests
{
    [Fact]
    public void WrapperPreservesExistingArgumentsEnvironmentAndPrivateCommand()
    {
        var options = new InstanceLaunchOptions { LaunchCommand = "prime-run %command%", Arguments = ["--flag", "literal $HOME; value"], Environment = new() { ["EXAMPLE"] = "1" } };
        var env = InstanceLaunchPlan.Environment(options, new Dictionary<string, string?> { ["XODUS_SOCKET"] = "/private/socket" });
        var command = InstanceLaunchPlan.Create("/private/xodus-cli", "/game path", "/private/wine", "game.exe", "/game path", options, env);
        Assert.Equal("prime-run", command.Executable);
        Assert.Equal("/private/xodus-cli", command.Arguments[0]);
        Assert.Equal("literal $HOME; value", command.Arguments.Last());
        Assert.Equal("/private/socket", command.Environment!["XODUS_SOCKET"]);
        Assert.Equal("1", command.Environment["EXAMPLE"]);
        Assert.Contains("--offline-license", command.Arguments);
    }

    [Theory]
    [InlineData("prime-run")]
    [InlineData("%command% %command%")]
    [InlineData("prime-run %command%; other")]
    [InlineData("prime-run '%command%")]
    [InlineData("prime-run prefix%command%")]
    [InlineData("%command% suffix")]
    public void InvalidTemplatesAreRejected(string template) => Assert.Throws<ArgumentException>(() => LaunchCommandTemplate.Parse(template));

    [Fact]
    public void QuotedWrapperAndResetRoundTrip()
    {
        Assert.Equal(new[] { "/tools/my wrapper", "--option", "%command%" }, LaunchCommandTemplate.Parse("'/tools/my wrapper' --option %command%"));
        var vm = new LaunchOptionsViewModel(GameInstance.Create("Test", "1", "Release") with { LaunchOptions = new() { LaunchCommand = "prime-run %command%" } });
        Assert.Equal("prime-run %command%", vm.Build().LaunchCommand);
        vm.ResetCommand.Execute(null); Assert.Equal("%command%", vm.Build().LaunchCommand);
    }
}

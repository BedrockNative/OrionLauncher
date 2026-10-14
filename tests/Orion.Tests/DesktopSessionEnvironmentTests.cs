using Orion.Infrastructure.Processes;

namespace Orion.Tests;

public sealed class DesktopSessionEnvironmentTests
{
    [Theory]
    [InlineData("wayland-0")]
    [InlineData("/run/user/1000/wayland-2")]
    public void RestoresTheOriginalSocketAfterGtkChangesTheEnvironment(string socket)
    {
        var environment = new Dictionary<string, string?>
        {
            ["WAYLAND_DISPLAY"] = DesktopSessionEnvironment.GtkDisabledDisplay,
            ["DISPLAY"] = ":0"
        };
        new DesktopSessionEnvironment(socket).Restore(environment);
        Assert.Equal(socket, environment["WAYLAND_DISPLAY"]);
        Assert.Equal(":0", environment["DISPLAY"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(DesktopSessionEnvironment.GtkDisabledDisplay)]
    public void DoesNotInventASocketForAnX11Session(string? socket)
    {
        var environment = new Dictionary<string, string?> { ["WAYLAND_DISPLAY"] = DesktopSessionEnvironment.GtkDisabledDisplay };
        new DesktopSessionEnvironment(socket).Restore(environment);
        Assert.False(environment.ContainsKey("WAYLAND_DISPLAY"));
    }

    [Theory]
    [InlineData("wayland-9")]
    [InlineData("")]
    public void PreservesOtherDisplayValues(string current)
    {
        var environment = new Dictionary<string, string?> { ["WAYLAND_DISPLAY"] = current };
        new DesktopSessionEnvironment("wayland-0").Restore(environment);
        Assert.Equal(current, environment["WAYLAND_DISPLAY"]);
    }

    [Fact]
    public void PreservesAnUnsetDisplay()
    {
        var environment = new Dictionary<string, string?>();
        new DesktopSessionEnvironment("wayland-0").Restore(environment);
        Assert.Empty(environment);
    }
}

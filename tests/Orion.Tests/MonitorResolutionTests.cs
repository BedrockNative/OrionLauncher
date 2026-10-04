using Orion.Desktop.ViewModels;
using Orion.Domain;

namespace Orion.Tests;

public sealed class MonitorResolutionTests
{
    [Fact]
    public void MonitorSelectionLocksDimensionsPersistsAndSurvivesDisconnection()
    {
        var instance = GameInstance.Create("Monitor", "26.50", "Release");
        var model = new LaunchOptionsViewModel(instance);
        model.SetMonitorResolutions([new("External · 2560 × 1440", new(2560, 1440)), new("Laptop · 1920 × 1200", new(1920, 1200))]);
        Assert.False(model.ResolutionIsLocked);
        model.SelectedMonitorResolution = model.MonitorResolutions[1];
        Assert.True(model.CustomResolution); Assert.True(model.ResolutionIsLocked); Assert.False(model.CanEditResolution);
        Assert.Equal("2560", model.Width); Assert.Equal("1440", model.Height);
        var options = model.Build(); Assert.True(options.ResolutionLocked);
        model.Width = "invalid"; Assert.Equal(options.Resolution, model.Build().Resolution);
        model = new(instance with { LaunchOptions = options });
        model.SetMonitorResolutions([]);
        Assert.True(model.ResolutionIsLocked); Assert.Equal(new(2560, 1440), model.Build().Resolution);
        model.SelectedMonitorResolution = model.MonitorResolutions[0];
        Assert.True(model.CanEditResolution); Assert.False(model.Build().ResolutionLocked);
        model.ResetCommand.Execute(null); Assert.False(model.CustomResolution); Assert.Null(model.Build().Resolution);
    }

    [Fact]
    public void InvalidMonitorsAreIgnoredAndHotplugDoesNotOverwriteManualValues()
    {
        var model = new LaunchOptionsViewModel(GameInstance.Create("Monitor", "26.50", "Release")) { CustomResolution = true, Width = "1600", Height = "900" };
        model.SetMonitorResolutions([new("Invalid", new(0, 0)), new("Connected", new(1920, 1080))]);
        Assert.Equal(2, model.MonitorResolutions.Count); Assert.Equal("1600", model.Width);
        Assert.True(model.CanEditResolution);
    }
}

using Orion.Desktop.I18n;
using Orion.Desktop.ViewModels;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

public sealed class RuntimeUpdatesTests
{
    [Fact]
    public async Task DisabledCheckDoesNotContactServersAndDefaultsRemainEnabledForOldSettings()
    {
        var model = new RuntimeUpdatesViewModel(new Localizer(), (_, _) => throw new InvalidOperationException("No network"));
        await model.CheckOnStartupAsync(false);
        Assert.False(model.HasUpdates);
        Assert.False(model.IsChecking);
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.Root, "settings.json");
        await File.WriteAllTextAsync(file, "{}");
        Assert.True((await AtomicFile.ReadJsonAsync<LauncherSettings>(file))!.CheckRuntimeUpdatesOnStartup);
    }

    [Fact]
    public async Task ShutdownBeforeTheWindowOpensPreventsALateStartupCheck()
    {
        var model = new RuntimeUpdatesViewModel(new Localizer(), (_, _) => throw new InvalidOperationException("No network"));
        await model.StopAsync();
        await model.CheckOnStartupAsync(true);
        Assert.False(model.IsChecking);
        Assert.False(model.HasUpdates);
    }

    [Fact]
    public async Task ChecksBothRuntimesOnceAndReportsOnlyAvailableUpdates()
    {
        List<string> calls = [];
        var model = new RuntimeUpdatesViewModel(new Localizer(), (runtime, _) =>
        {
            calls.Add(runtime.Name);
            return Task.FromResult(new RuntimeUpdate(runtime.Name, "1", runtime == RuntimeDefinition.Xodus ? "2" : "v1"));
        });
        await model.CheckOnStartupAsync(true);
        await model.CheckOnStartupAsync(true);
        Assert.Equal(new[] { "xodus", "winegdk" }, calls);
        Assert.True(model.HasUpdates);
        Assert.Contains("xodus: 2", model.Status);
        Assert.DoesNotContain("winegdk:", model.Status);
        model.Clear();
        Assert.False(model.HasUpdates);
    }

    [Fact]
    public async Task FailureInOneCheckStillAllowsTheOtherToReportAnUpdate()
    {
        var model = new RuntimeUpdatesViewModel(new Localizer(), (runtime, _) =>
            runtime == RuntimeDefinition.Xodus ? throw new HttpRequestException("Offline")
                : Task.FromResult(new RuntimeUpdate(runtime.Name, "1", "2")));
        await model.CheckOnStartupAsync(true);
        Assert.True(model.HasUpdates);
        Assert.Contains("winegdk: 2", model.Status);
        Assert.Contains("xodus", model.Status);
        Assert.False(model.IsChecking);
    }

    [Fact]
    public async Task BackgroundCheckCanBeCancelledAndAwaitedOnShutdown()
    {
        var calls = 0;
        var model = new RuntimeUpdatesViewModel(new Localizer(), async (runtime, ct) =>
        {
            calls++;
            await Task.Delay(Timeout.Infinite, ct);
            return new(runtime.Name, "1", "2");
        });
        var pending = model.CheckOnStartupAsync(true);
        Assert.False(pending.IsCompleted);
        Assert.True(model.IsChecking);
        await model.StopAsync();
        Assert.True(pending.IsCompletedSuccessfully);
        Assert.False(model.IsChecking);
        Assert.False(model.HasUpdates);
        Assert.Equal("", model.Status);
        Assert.Equal(1, calls);
    }
}

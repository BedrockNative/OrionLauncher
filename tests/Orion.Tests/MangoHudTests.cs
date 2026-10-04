using Orion.Desktop.ViewModels;
using Orion.Domain;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

public sealed class MangoHudTests
{
    private static string InstallFixture(TestDirectory directory)
    {
        var executable = Path.Combine(directory.Root, "mangohud");
        // Detection must not execute arbitrary PATH programs.
        File.WriteAllText(executable, "not an executable program");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return executable;
    }

    [Fact]
    public void DetectionRequiresAnExecutableAndIgnoresRelativeDirectories()
    {
        using var directory = new TestDirectory();
        Assert.Null(MangoHudIntegration.FindExecutable(directory.Root));
        var executable = InstallFixture(directory);
        Assert.Equal(executable, MangoHudIntegration.FindExecutable(":" + directory.Root + ":."));
        Assert.Null(MangoHudIntegration.FindExecutable(".:relative/path:"));
        File.SetUnixFileMode(executable, UnixFileMode.UserRead);
        Assert.Null(MangoHudIntegration.FindExecutable(directory.Root));
    }

    [Fact]
    public void MissingOrDisabledDoesNotBlockLaunchOrChangeAdvancedSettings()
    {
        using var directory = new TestDirectory();
        var env = new Dictionary<string, string?> { ["PATH"] = directory.Root, ["MANGOHUD_CONFIG"] = "fps_only", ["XODUS_SOCKET"] = "/private/socket" };
        List<string> messages = [];
        Assert.Equal(env, MangoHudIntegration.Apply(new(), env, messages.Add));
        Assert.Contains("not found", Assert.Single(messages));
        InstallFixture(directory);
        Assert.Equal(env, MangoHudIntegration.Apply(new() { MangoHud = new() { Enabled = false } }, env));
    }

    [Fact]
    public void InstalledHudUsesOnlyTheGameEnvironmentAndPreservesWrappersAndLiteralArguments()
    {
        using var directory = new TestDirectory();
        InstallFixture(directory);
        var options = new InstanceLaunchOptions { LaunchCommand = "prime-run %command%", Arguments = ["two words", "$HOME"], MangoHud = new() { Cpu = false, Ram = false } };
        var defaults = new Dictionary<string, string?> { ["PATH"] = directory.Root, ["XODUS_SOCKET"] = "/private/socket", ["MANGOHUD_CONFIG"] = "full" };
        var env = MangoHudIntegration.Apply(options, defaults);
        var command = InstanceLaunchPlan.Create("/xodus", "/game", "/wine", "Game.exe", "/game", options, env);
        Assert.Equal("prime-run", command.Executable);
        Assert.Equal(["two words", "$HOME"], command.Arguments.TakeLast(2));
        Assert.Equal("1", command.Environment!["MANGOHUD"]);
        Assert.Equal("/private/socket", env["XODUS_SOCKET"]);
        Assert.Contains("cpu_stats=0", env["MANGOHUD_CONFIG"]);
        Assert.Contains("ram=0", env["MANGOHUD_CONFIG"]);
        Assert.DoesNotContain("LD_PRELOAD", env.Keys);
        Assert.DoesNotContain("MANGOHUD", defaults.Keys);
        Assert.Equal("full", defaults["MANGOHUD_CONFIG"]);
        Assert.Equal("1", ProcessRunner.StartInfo(command).Environment["MANGOHUD"]);
    }

    [Fact]
    public void FiltersExplicitlyDisableDefaultsAndDoNotEnableGlobalFilesOrPerformanceOverrides()
    {
        var hud = new MangoHudOptions { Fps = false, Cpu = false, Gpu = false, Ram = false,
            CpuTemperature = true, GpuTemperature = true, Position = MangoHudPosition.BottomRight };
        var config = MangoHudIntegration.Configuration(hud);
        foreach (var name in new[] { "fps", "frametime", "frame_timing", "cpu_stats", "gpu_stats", "ram", "vram", "cpu_temp", "gpu_temp", "battery", "resolution" })
            Assert.Contains(name + "=0", config.Split(','));
        Assert.Contains("no_display=1", config);
        Assert.Contains("position=bottom-right", config);
        Assert.DoesNotContain("read_cfg", config);
        Assert.DoesNotContain("fps_limit", config);
        Assert.DoesNotContain("autostart_log", config);
        Assert.DoesNotContain("exec=", config);
        Assert.Throws<ArgumentException>(() => (hud with { Position = (MangoHudPosition)999 }).Validate());
    }

    [Fact]
    public async Task PreferencesPersistPerInstanceAndEditorResetsToAutomaticDefaults()
    {
        using var directory = new TestDirectory();
        var repo = new InstanceRepository(directory.Paths);
        var instance = GameInstance.Create("HUD", "26.30", "Release");
        var other = GameInstance.Create("Other", "26.30", "Release");
        var editor = new LaunchOptionsViewModel(instance)
        {
            MangoHudEnabled = false, MangoHudFps = false, MangoHudFrameTime = true,
            MangoHudCpuTemperature = true, MangoHudGpuTemperature = true, MangoHudVram = true,
            MangoHudBattery = true, MangoHudResolution = true
        };
        editor.SelectedMangoHudPosition = editor.MangoHudPositions[2];
        await repo.SaveAsync(instance with { LaunchOptions = editor.Build() });
        await repo.SaveAsync(other);
        var saved = (await repo.ListAsync()).Single(i => i.Id == instance.Id);
        Assert.Equal(editor.Build().MangoHud, new LaunchOptionsViewModel(saved).Build().MangoHud);
        Assert.Equal(new MangoHudOptions(), (await repo.ListAsync()).Single(i => i.Id == other.Id).LaunchOptions!.MangoHud);
        editor.ResetCommand.Execute(null);
        Assert.Equal(new MangoHudOptions(), editor.Build().MangoHud);
        Assert.Equal(new MangoHudOptions(), new LaunchOptionsViewModel(instance with { LaunchOptions = new() { MangoHud = null } }).Build().MangoHud);
    }
}

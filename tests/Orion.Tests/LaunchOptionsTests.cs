using System.Text.Json;
using Orion.Application;
using Orion.Desktop.ViewModels;
using Orion.Domain;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Runtime;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

public sealed class LaunchOptionsTests
{
    [Fact]
    public async Task ContentLeaseAlsoPreventsArchivingOrReconfiguringTheInstance()
    {
        using var directory = new TestDirectory();
        var repository = new InstanceRepository(directory.Paths);
        var instance = GameInstance.Create("Shared content target", "1", "Release"); await repository.SaveAsync(instance);
        var activity = new InstanceActivity();
        var service = new InstanceService(repository, new UnusedInstaller(), new RecordingLauncher(), new NoDesktop(), activity);
        using (activity.Acquire(instance.Id))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ArchiveAsync(instance.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetConfigurationAsync(instance.Id, "Changed", new(), null, false));
            Assert.Equal(instance.Name, (await repository.GetAsync(instance.Id)).Name);
        }
        await service.ArchiveAsync(instance.Id);
        Assert.Empty(await repository.ListAsync());
    }
    [Fact]
    public async Task ConfigurationValidatesAllFieldsBeforeSavingAndRejectsRunningGames()
    {
        using var directory = new TestDirectory();
        var repository = new InstanceRepository(directory.Paths);
        var instance = GameInstance.Create("Original", "1", "Release");
        await repository.SaveAsync(instance);
        var launcher = new RecordingLauncher();
        var service = new InstanceService(repository, new UnusedInstaller(), launcher, new NoDesktop(), new Orion.Infrastructure.Games.InstanceActivity());
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetConfigurationAsync(instance.Id, " ", new(), null, true));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetConfigurationAsync(instance.Id, "Changed", new(), "../bad", true));
        Assert.Equal("Original", (await repository.GetAsync(instance.Id)).Name);
        Assert.False((await repository.GetAsync(instance.Id)).DesktopShortcut);
        launcher.Running = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetConfigurationAsync(instance.Id, "Changed", new(), null, true));
        Assert.Equal("Original", (await repository.GetAsync(instance.Id)).Name);
    }

    [Fact]
    public async Task OptionsArePersistedPerInstanceAndUsedByPlayById()
    {
        using var directory = new TestDirectory();
        var repository = new InstanceRepository(directory.Paths);
        var first = GameInstance.Create("One", "1", "Release");
        var second = GameInstance.Create("Two", "1", "Release");
        await repository.SaveAsync(first); await repository.SaveAsync(second);
        var launcher = new RecordingLauncher();
        var service = new InstanceService(repository, new UnusedInstaller(), launcher, new NoDesktop(), new Orion.Infrastructure.Games.InstanceActivity());
        var options = new InstanceLaunchOptions { Arguments = ["--flag", "two words"], Environment = new() { ["WINEDEBUG"] = "-all" }, Resolution = new(1280, 720), Fullscreen = true };
        await service.SetLaunchOptionsAsync(first.Id, options);
        await service.RenameAsync(first.Id, "Renamed");
        await service.PlayAsync(first.Id, null, CancellationToken.None);
        Assert.Equal(options.Arguments, launcher.Last!.LaunchOptions.Arguments);
        Assert.Equal(options.Resolution, launcher.Last.LaunchOptions.Resolution);
        Assert.Equal("-all", launcher.Last.LaunchOptions.Environment["WINEDEBUG"]);
        Assert.Empty((await repository.GetAsync(second.Id)).LaunchOptions.Arguments);
        Assert.NotNull((await repository.GetAsync(first.Id)).LastPlayedAt);
        launcher.Running = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetLaunchOptionsAsync(first.Id, new()));
    }

    [Fact]
    public void LegacyMetadataKeepsDefaultLaunchBehavior()
    {
        var instance = JsonSerializer.Deserialize<GameInstance>("""{"id":"00000000-0000-0000-0000-000000000001","name":"Old","version":"1","channel":"Release","createdAt":"2025-01-01T00:00:00Z"}""", AtomicFile.Json)!;
        Assert.Empty(instance.LaunchOptions.Arguments);
        Assert.Empty(instance.LaunchOptions.Environment);
        Assert.Null(instance.LaunchOptions.Resolution);
    }

    [Theory]
    [InlineData("XODUS_CONFIG_DIR")]
    [InlineData("XODUS_SOCK_NAME")]
    [InlineData("XODUS_SOCKET")]
    [InlineData("WINEPREFIX")]
    [InlineData("WINESERVER")]
    [InlineData("WINE_DLL_FILE_MAP")]
    [InlineData("HOME")]
    [InlineData("XDG_RUNTIME_DIR")]
    [InlineData("DBUS_SESSION_BUS_ADDRESS")]
    [InlineData("1INVALID")]
    [InlineData("BAD-NAME")]
    public void InvalidOrIsolationVariablesAreRejected(string key) =>
        Assert.Throws<ArgumentException>(() => new InstanceLaunchOptions { Environment = new() { [key] = "value" } }.Validate());

    [Fact]
    public void EnvironmentIsMergedWithoutChangingLauncherOrServiceDefaults()
    {
        using var directory = new TestDirectory();
        var defaults = new XodusEnvironment(directory.Paths).Create(directory.Paths.Prefix(Guid.NewGuid()));
        var merged = InstanceLaunchPlan.Environment(new() { Environment = new() { ["WINEDEBUG"] = "+fps", ["CUSTOM_VALUE"] = "a=b $HOME" } }, defaults);
        Assert.Equal("-all", defaults["WINEDEBUG"]);
        Assert.False(defaults.ContainsKey("CUSTOM_VALUE"));
        Assert.Equal("+fps", merged["WINEDEBUG"]);
        Assert.Equal("a=b $HOME", merged["CUSTOM_VALUE"]);
        Assert.Equal(defaults["XODUS_SOCKET"], merged["XODUS_SOCKET"]);
        Assert.Equal(defaults["WINEPREFIX"], merged["WINEPREFIX"]);
    }

    [Fact]
    public void GameArgumentsStayLiteralAndCannotChangeXodusOptions()
    {
        string[] args = ["--offline-license", "two words", "$(touch nope); $HOME", "世界"];
        var options = new InstanceLaunchOptions { Arguments = args };
        var command = InstanceLaunchPlan.Create("/xodus", "/game", "/wine", "Game.exe", "/game", options, new Dictionary<string, string?>());
        Assert.Equal("/xodus", command.Executable);
        Assert.Equal(["run", "/game", "/wine", "--exe", "Game.exe", "--offline-license", "--", .. args], command.Arguments);
        Assert.Throws<InvalidOperationException>(() => XodusGameCommands.RequireLaunchArgumentsApi(new("xodus", "0.2.1", "/runtime"), options));
        XodusGameCommands.RequireLaunchArgumentsApi(new("xodus", "v0.3.0", "/runtime"), options);
        XodusGameCommands.RequireLaunchArgumentsApi(new("xodus", "0.2.1", "/runtime"), new());
    }

    [Fact]
    public void DisplayPreferencesDoNotChangeLaunchArgumentsOrEnvironmentAndCanBeReset()
    {
        var options = new InstanceLaunchOptions
        {
            Arguments = ["--flag", "two words"], Environment = new() { ["WINEDEBUG"] = "-all" },
            Resolution = new(1920, 1080), Fullscreen = true
        };
        var defaults = new Dictionary<string, string?> { ["XODUS_SOCKET"] = "/run/orion.xodus-test.sock" };
        var env = InstanceLaunchPlan.Environment(options, defaults);
        var command = InstanceLaunchPlan.Create("/xodus path", "/game", "/wine", "Game.exe", "/game", options, env);
        var withoutDisplay = options with { Resolution = null, Fullscreen = false };
        var plainEnv = InstanceLaunchPlan.Environment(withoutDisplay, defaults);
        var plainCommand = InstanceLaunchPlan.Create("/xodus path", "/game", "/wine", "Game.exe", "/game", withoutDisplay, plainEnv);
        Assert.Equal("/xodus path", command.Executable);
        Assert.Equal(plainCommand.Arguments, command.Arguments);
        Assert.Equal(plainCommand.WorkingDirectory, command.WorkingDirectory);
        Assert.Equal(plainEnv.OrderBy(p => p.Key), env.OrderBy(p => p.Key));
        Assert.Equal(["run", "/game", "/wine", "--exe", "Game.exe", "--offline-license", "--", "--flag", "two words"], command.Arguments);
        var editor = new LaunchOptionsViewModel(GameInstance.Create("Test", "1", "Release") with { LaunchOptions = options });
        editor.ResetCommand.Execute(null);
        Assert.Null(editor.Build().Resolution);
        Assert.False(editor.Build().Fullscreen);
    }

    [Theory]
    [InlineData(0, 720)]
    [InlineData(1280, -1)]
    [InlineData(99999, 1080)]
    public void InvalidResolutionIsRejected(int width, int height) =>
        Assert.Throws<ArgumentException>(() => new InstanceLaunchOptions { Resolution = new(width, height) }.Validate());

    [Fact]
    public void InvalidSavedDisplayPlaceholdersCannotBlockLaunch()
    {
        foreach (var resolution in new GameResolution?[] { new(0, -1), null })
        {
            var options = new InstanceLaunchOptions { Resolution = resolution, Fullscreen = true };
            Assert.Throws<ArgumentException>(() => options.Validate()); // Still validate editor saves.
            var env = InstanceLaunchPlan.Environment(options, new Dictionary<string, string?>());
            var command = InstanceLaunchPlan.Create("/xodus", "/game", "/wine", "Game.exe", "/game", options, env);
            Assert.Equal("/xodus", command.Executable);
            Assert.Equal(["run", "/game", "/wine", "--exe", "Game.exe", "--offline-license"], command.Arguments);
            Assert.Empty(env);
        }
    }

    [Fact]
    public async Task ProcessReceivesEnvironmentValuesWithoutShellExpansion()
    {
        using var directory = new TestDirectory();
        const string literal = "a=b $HOME $(touch nope)";
        var env = InstanceLaunchPlan.Environment(new() { Environment = new() { ["ORION_TEST_VALUE"] = literal } }, new Dictionary<string, string?>());
        var log = Path.Combine(directory.Root, "environment.log");
        await new ProcessRunner().RunAsync(new("/bin/sh", ["-c", "printf '%s' \"$ORION_TEST_VALUE\""], directory.Root, env), log, CancellationToken.None);
        Assert.Contains(literal, await File.ReadAllTextAsync(log));
        Assert.False(File.Exists(Path.Combine(directory.Root, "nope")));
    }

    [Fact]
    public void EditorParsesLiteralLinesAndRejectsMalformedVariables()
    {
        var editor = new LaunchOptionsViewModel(GameInstance.Create("Test", "1", "Release"))
        { ArgumentLines = "--flag\r\ntwo words\n$HOME\n", EnvironmentLines = "CUSTOM=a=b $HOME\nEMPTY=\n" };
        Assert.Equal(["--flag", "two words", "$HOME"], editor.Build().Arguments);
        Assert.Equal("a=b $HOME", editor.Build().Environment["CUSTOM"]);
        Assert.Equal("", editor.Build().Environment["EMPTY"]);
        foreach (var invalid in new[] { "MISSING_EQUALS", "DUP=1\nDUP=2", "WINEPREFIX=/other" })
        { editor.EnvironmentLines = invalid; Assert.Throws<ArgumentException>(() => editor.Build()); }
        editor.EnvironmentLines = ""; editor.CustomResolution = true; editor.Width = "12.5";
        Assert.Throws<ArgumentException>(() => editor.Build());
    }

    private sealed class RecordingLauncher : IGameLauncher
    {
        public bool Running { get; set; }
        public GameInstance? Last { get; private set; }
        public bool IsRunning(Guid id) => Running;
        public Task RunAsync(GameInstance instance, IProgress<OperationProgress>? progress, CancellationToken ct)
        { Last = instance; return Task.CompletedTask; }
    }
    private sealed class UnusedInstaller : IGameInstaller
    {
        public Task InstallAsync(GameInstance instance, string source, IProgress<OperationProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class NoDesktop : IDesktopIntegration
    {
        public Task SynchronizeAsync(IReadOnlyList<GameInstance> instances, CancellationToken ct = default) => Task.CompletedTask;
    }
}

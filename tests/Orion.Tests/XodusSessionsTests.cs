using System.Diagnostics;
using Orion.Application;
using Orion.Domain;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Rtx;
using Orion.Infrastructure.Runtime;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

public sealed class XodusSessionsTests
{
    private static async Task Executable(string path, string source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, source);
        File.SetUnixFileMode(path, (UnixFileMode)0x1ED);
    }

    internal static async Task<RuntimeInstallation> Runtime(TestDirectory dir)
    {
        var root = Path.Combine(dir.Root, "bundle/xodus");
        await Executable(Path.Combine(root, "xodus-service"), """
            #!/usr/bin/python3
            import os, socket
            with open(os.path.join(os.environ['XODUS_CONFIG_DIR'], 'service-starts'), 'a') as f:
                f.write(str(os.getpid()) + '\n')
            server = socket.socket(socket.AF_UNIX)
            server.bind(os.environ['XODUS_SOCKET'])
            server.listen(16)
            while True:
                client, _ = server.accept()
                print('fixture socket probe', flush=True)
                client.close()
            """);
        await Executable(Path.Combine(root, "xodus-cli"), """
            #!/bin/sh
            if [ "$1" = accounts ]; then
                printf '[]'
            else
                printf 'game\n' >> "$XODUS_CONFIG_DIR/commands"
            fi
            """);
        await AtomicFile.WriteJsonAsync(Path.Combine(root, "version.json"), "0.7.3");
        return new("xodus", "0.7.3", root);
    }

    private static int[] Starts(TestDirectory dir) => File.ReadAllLines(Path.Combine(dir.Paths.XodusProfile, "service-starts")).Select(int.Parse).ToArray();
    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task StartupWarmsDistinctAccountsAndRepeatedLaunchesReuseTheirService()
    {
        using var dir = new TestDirectory();
        var runtime = await Runtime(dir);
        await using var sessions = new XodusSessions(dir.Paths, new());
        var first = new string('a', 64); var second = new string('b', 64);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await sessions.WarmAsync(runtime, [first, second, first], timeout.Token);
        var initialPids = Starts(dir);
        Assert.Equal(2, initialPids.Length);
        var a = await sessions.GetAsync(runtime, first, timeout.Token);
        var b = await sessions.GetAsync(runtime, second, timeout.Token);
        var sameAccount = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => sessions.GetAsync(runtime, first, timeout.Token)));
        Assert.All(sameAccount, session => Assert.Same(a, session));
        Assert.NotEqual(a.Environment.SocketPath, b.Environment.SocketPath);
        Assert.Equal(first, a.Environment.Create()["XODUS_ACCOUNT_ID"]);
        Assert.Equal(second, b.Environment.Create()["XODUS_ACCOUNT_ID"]);
        Assert.Equal(initialPids, Starts(dir));
        await sessions.ResetAsync(timeout.Token);
        Assert.All(initialPids, pid => Assert.False(Alive(pid)));
        var renewed = await sessions.GetAsync(runtime, first, timeout.Token);
        Assert.NotSame(a, renewed);
        Assert.Equal(3, Starts(dir).Length);
        await sessions.DisposeAsync();
        Assert.All(Starts(dir), pid => Assert.False(Alive(pid)));
    }

    [Fact]
    public async Task CancelledResetLeavesTheCurrentServiceUsable()
    {
        using var dir = new TestDirectory();
        var runtime = await Runtime(dir);
        await using var sessions = new XodusSessions(dir.Paths, new());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ready = await sessions.GetAsync(runtime, null, timeout.Token);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sessions.ResetAsync(cancelled.Token));
        Assert.Same(ready, await sessions.GetAsync(runtime, null, timeout.Token));
        Assert.Single(Starts(dir));
    }

    [Fact]
    public async Task ResetCancelsAServiceStillWaitingForStartup()
    {
        using var dir = new TestDirectory();
        var runtime = await Runtime(dir);
        await Executable(Path.Combine(runtime.Directory, "xodus-service"), "#!/bin/sh\nexec sleep 60\n");
        await using var sessions = new XodusSessions(dir.Paths, new());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = sessions.GetAsync(runtime, null, timeout.Token);
        await Task.Delay(100, timeout.Token);
        await sessions.ResetAsync(timeout.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task SlowAccountStartupDoesNotDelayAnAlreadyReadyAccount()
    {
        using var dir = new TestDirectory();
        var runtime = await Runtime(dir);
        var service = Path.Combine(runtime.Directory, "xodus-service");
        var script = await File.ReadAllTextAsync(service);
        await File.WriteAllTextAsync(service, script.Replace("server = socket.socket",
            "if os.environ.get('XODUS_ACCOUNT_ID', '').startswith('b'):\n    import time\n    time.sleep(60)\nserver = socket.socket"));
        await using var sessions = new XodusSessions(dir.Paths, new());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var first = new string('a', 64);
        var ready = await sessions.GetAsync(runtime, first, timeout.Token);
        var slow = sessions.GetAsync(runtime, new string('b', 64), timeout.Token);
        for (var i = 0; i < 100 && Starts(dir).Length < 2; i++) await Task.Delay(50, timeout.Token);
        Assert.Equal(2, Starts(dir).Length);
        Assert.Same(ready, await sessions.GetAsync(runtime, first, timeout.Token).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(slow.IsCompleted);
        await sessions.ResetAsync(timeout.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);
    }

    [Fact]
    public async Task GameLaunchReusesStartupServiceAndRunsNoPreparatoryWineCommand()
    {
        using var dir = new TestDirectory();
        var runtime = await Runtime(dir);
        var wine = Path.Combine(dir.Root, "bundle/winegdk");
        foreach (var name in new[] { "wine", "wineboot" })
            await Executable(Path.Combine(wine, name), "#!/bin/sh\nprintf 'unexpected-wine-preparation\\n' >> \"$XODUS_CONFIG_DIR/commands\"\nexit 99\n");
        await Executable(Path.Combine(wine, "wineserver"), "#!/bin/sh\nprintf 'server %s\\n' \"$1\" >> \"$XODUS_CONFIG_DIR/commands\"\n");
        await AtomicFile.WriteJsonAsync(Path.Combine(wine, "version.json"), "11.18-8-winrt");
        using var http = new HttpClient(new ReleaseTests.Handler(_ => throw new InvalidOperationException("No network")));
        var runtimes = new RuntimeManager(dir.Paths, new NoReleases(), new(http), Path.Combine(dir.Root, "bundle"));
        var runner = new ProcessRunner();
        await using var sessions = new XodusSessions(dir.Paths, runner);
        await using var management = new XodusService(dir.Paths, new(dir.Paths), runner);
        var accounts = new AccountService(dir.Paths, runtimes, runner, new(dir.Paths), management, sessions);
        var activity = new InstanceActivity();
        var content = new InstanceContentService(dir.Paths, activity);
        var rtx = new RtxService(dir.Paths, activity, new RtxTests.Catalog(), content);
        var launcher = new GameLauncher(dir.Paths, runtimes, runner, accounts, activity, content, rtx, sessions);
        var instance = GameInstance.Create("Disposable launch fixture", "26.52.03", "Release");
        await new InstanceRepository(dir.Paths).SaveAsync(instance);
        var game = dir.Paths.Game(instance.Id); Directory.CreateDirectory(game);
        await File.WriteAllTextAsync(Path.Combine(game, "Minecraft.Windows.exe"), "fixture");
        await File.WriteAllTextAsync(Path.Combine(game, ".xodus-streaming.msixvc"), "fixture");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await sessions.WarmAsync(runtime, [null], timeout.Token);
        var pid = Assert.Single(Starts(dir));
        await launcher.RunAsync(instance, null, timeout.Token);
        await launcher.RunAsync(instance, null, timeout.Token);
        Assert.Equal(pid, Assert.Single(Starts(dir)));
        Assert.True(Alive(pid));
        Assert.Equal(new[] { "game", "server -w", "game", "server -w" },
            File.ReadAllLines(Path.Combine(dir.Paths.XodusProfile, "commands")));
        var log = await File.ReadAllTextAsync(dir.Paths.InstanceLog(instance.Id));
        Assert.Contains("[Launch timing] Xodus service ready:", log);
        Assert.Contains("[Launch timing] RTX validation and preferences:", log);
        Assert.Contains("[RTX timing] Recovery and game scan:", log);
        Assert.Contains("[RTX timing] Shader integrity and compatibility:", log);
        Assert.Contains("[RTX timing] RTX families and content:", log);
        Assert.Contains("[Launch timing] Game diagnostics:", log);
        Assert.False(launcher.IsRunning(instance.Id));
    }

    private sealed class NoReleases : IReleaseClient
    {
        public Task<Release> GetLatestAsync(Repository repository, CancellationToken ct) => throw new InvalidOperationException("No network");
    }
}

[Collection("Avalonia UI")]
public sealed class XodusStartupUiTests
{
    [Fact]
    public async Task OpeningLauncherStartsXodusWithoutLaunchingAGameAndShutdownStopsIt()
    {
        using var session = Avalonia.Headless.HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var dir = new TestDirectory();
            var runtime = await XodusSessionsTests.Runtime(dir);
            await AtomicFile.WriteJsonAsync(Path.Combine(dir.Paths.Tools, "xodus/current.json"), runtime);
            await using var services = new Orion.Desktop.Composition.LauncherServices(dir.Paths, new(),
                ["dotnet", typeof(Orion.Desktop.App).Assembly.Location]);
            var window = new Orion.Desktop.Views.MainWindow();
            var model = new Orion.Desktop.ViewModels.MainViewModel(services, window);
            window.DataContext = model;
            window.Show();
            try
            {
                await model.InitializeAsync();
                model.StartXodusOnStartup();
                var starts = Path.Combine(dir.Paths.XodusProfile, "service-starts");
                for (var i = 0; i < 100 && !File.Exists(starts); i++) await Task.Delay(50);
                Assert.True(File.Exists(starts), "Opening Orion should prepare Xodus before Play.");
                var pid = int.Parse(Assert.Single(await File.ReadAllLinesAsync(starts)));
                model.StartXodusOnStartup();
                Assert.Single(await File.ReadAllLinesAsync(starts));
                Assert.False(model.IsBusy);
                Assert.Empty(model.Instances);
                var pooled = await services.XodusSessions.GetAsync(runtime, null, default);
                var outputThread = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                pooled.Service.OutputReceived += _ => outputThread.TrySetResult(Avalonia.Threading.Dispatcher.UIThread.CheckAccess());
                await services.XodusSessions.GetAsync(runtime, null, default);
                Assert.False(await outputThread.Task.WaitAsync(TimeSpan.FromSeconds(5)), "Service output must not be drained on the UI thread.");
                await model.StopAllAsync();
                await services.DisposeAsync();
                Assert.False(Directory.Exists($"/proc/{pid}"));
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }
}

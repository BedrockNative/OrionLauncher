using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
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

[Collection("Avalonia UI")]
public sealed class GameLaunchThreadingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationAndPostAwaitWorkLeaveTheUiResponsive(bool cancelPreparation)
    {
        using var ui = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await ui.Dispatch<bool>(async () =>
        {
            using var dir = new TestDirectory();
            await XodusSessionsTests.Runtime(dir);
            var wine = Path.Combine(dir.Root, "bundle/winegdk"); Directory.CreateDirectory(wine);
            foreach (var name in RuntimeDefinition.WineGdk.Executables)
            {
                var path = Path.Combine(wine, name);
                await File.WriteAllTextAsync(path, "#!/bin/sh\nexit 0\n");
                File.SetUnixFileMode(path, (UnixFileMode)0x1ED);
            }
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
            var instance = await RtxTests.Setup(dir);
            await File.WriteAllTextAsync(Path.Combine(dir.Paths.Game(instance.Id), ".xodus-streaming.msixvc"), "fixture");
            var label = new TextBlock();
            var window = new Window { Content = label }; window.Show();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var probe = new ProgressProbe(new Progress<OperationProgress>(p =>
            {
                Assert.True(Dispatcher.UIThread.CheckAccess());
                label.Text = p.Message;
            }));
            var launch = launcher.RunAsync(instance, probe, cancellation.Token);
            try
            {
                await probe.Preparing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(probe.Preparing.OnUiThread);
                // The worker is deliberately held at the preparation boundary.
                // UI jobs must still run, including posted progress updates.
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Preparing game launch", label.Text);
                if (cancelPreparation) cancellation.Cancel();
                probe.Preparing.Release.Set();
                if (cancelPreparation)
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launch);
                    Assert.False(File.Exists(Path.Combine(dir.Paths.XodusProfile, "commands")));
                }
                else
                {
                    await probe.Playing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.False(probe.Playing.OnUiThread);
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal("Playing " + instance.Name, label.Text);
                    probe.Playing.Release.Set();
                    await launch;
                }
                Assert.False(launcher.IsRunning(instance.Id));
                using var lease = activity.Acquire(instance.Id); // Cancellation/completion released ownership.
            }
            finally
            {
                cancellation.Cancel(); probe.Preparing.Release.Set(); probe.Playing.Release.Set();
                try { await launch; } catch (OperationCanceledException) { }
                window.Close();
            }
            return true;
        }, CancellationToken.None);
    }

    private sealed class ProgressProbe(IProgress<OperationProgress> uiProgress) : IProgress<OperationProgress>, IDisposable
    {
        public sealed class Phase : IDisposable
        {
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public ManualResetEventSlim Release { get; } = new();
            public bool OnUiThread;
            public void Dispose() => Release.Dispose();
        }
        public Phase Preparing { get; } = new();
        public Phase Playing { get; } = new();
        public void Report(OperationProgress value)
        {
            uiProgress.Report(value);
            var phase = value.Message == "Preparing game launch" ? Preparing
                : value.Message.StartsWith("Playing ", StringComparison.Ordinal) ? Playing : null;
            if (phase is null) return;
            phase.OnUiThread = Dispatcher.UIThread.CheckAccess();
            phase.Entered.TrySetResult();
            if (!phase.Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The UI could not release launch preparation.");
        }
        public void Dispose() { Preparing.Dispose(); Playing.Dispose(); }
    }

    private sealed class NoReleases : IReleaseClient
    {
        public Task<Release> GetLatestAsync(Repository repository, CancellationToken ct) => throw new InvalidOperationException("No network");
    }
}

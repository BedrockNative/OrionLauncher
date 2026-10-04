using System.Collections.Concurrent;
using System.Diagnostics;
using Orion.Application;
using Orion.Domain;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Runtime;
using Orion.Infrastructure.Rtx;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Games;

public sealed class GameLauncher(AppPaths paths, RuntimeManager runtimes, ProcessRunner runner,
    AccountService accounts, InstanceActivity activity, Orion.Infrastructure.Content.InstanceContentService content,
    Orion.Infrastructure.Rtx.RtxService rtx, XodusSessions sessions) : IGameLauncher
{
    private readonly ConcurrentDictionary<Guid, byte> running = new();
    public bool IsRunning(Guid id) => running.ContainsKey(id);

    // Preparation contains synchronous filesystem scans and shader validation.
    // Start the whole pipeline off the caller's UI context, including subsequent
    // continuations, process-output draining and diagnostic polling. Progress<T>
    // is created by the caller and retains its own UI synchronization context.
    public Task RunAsync(GameInstance instance, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => RunCoreAsync(instance, progress, cancellationToken), cancellationToken);

    private async Task RunCoreAsync(GameInstance instance, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var activityLease = activity.Acquire(instance.Id);
        if (!running.TryAdd(instance.Id, 0)) throw new InvalidOperationException("This instance is already running.");
        using var journal = new InstanceJournal(paths, instance.Id, null, progress);
        progress = journal;
        var preparation = Stopwatch.StartNew();
        var previous = 0L;
        void Checkpoint(string stage)
        {
            var elapsed = preparation.ElapsedMilliseconds;
            ProcessLog.Event(journal.Path, "Launch timing", $"{stage}: {elapsed - previous} ms; total {elapsed} ms");
            previous = elapsed;
            cancellationToken.ThrowIfCancellationRequested();
        }
        try
        {
            ProcessLog.Event(journal.Path, "Launch timing", "Preparing game launch");
            progress?.Report(new("Preparing game launch"));
            cancellationToken.ThrowIfCancellationRequested();
            content.Recover(instance.Id);
            Checkpoint("Content recovery");
            var options = instance.LaunchOptions ?? new();
            options.ValidateLaunchInputs();
            var game = paths.Game(instance.Id);
            var gameExecutable = GameLayout.ValidatedExecutable(game);
            Checkpoint("Game files");
            var xodus = await runtimes.EnsureAsync(RuntimeDefinition.Xodus, progress, cancellationToken);
            Checkpoint("Xodus runtime");
            var accountId = await accounts.ResolveForLaunchAsync(xodus, options.AccountId, cancellationToken);
            Checkpoint("Account selection");
            var session = await sessions.GetAsync(xodus, accountId, cancellationToken);
            journal.AttachService(session.Service);
            Checkpoint("Xodus service ready");
            XodusGameCommands.RequirePurchaseApi(xodus);
            XodusGameCommands.RequireLaunchArgumentsApi(xodus, options);
            var wine = await runtimes.EnsureAsync(RuntimeDefinition.WineGdk, progress, cancellationToken);
            Checkpoint("WineGDK runtime");
            var rtxActive = rtx.PrepareLaunchUnderLease(instance.Id, progress, cancellationToken,
                (stage, duration) => ProcessLog.Event(journal.Path, "RTX timing", $"{stage}: {duration.TotalMilliseconds:F1} ms"));
            Checkpoint("RTX validation and preferences");
            var env = session.Environment.Create(paths.Prefix(instance.Id));
            var explicitNgx = options.Environment.TryGetValue("NVIDIA_WINE_DLL_DIR", out var configuredNgx)
                ? configuredNgx : Environment.GetEnvironmentVariable("NVIDIA_WINE_DLL_DIR");
            var ngxDirectory = rtxActive ? RtxRuntimeEnvironment.FindDriverDirectory(explicitNgx) : null;
            if (ngxDirectory is not null && !RtxRuntimeEnvironment.SupportsNgxDiscovery(wine.Tag))
            {
                progress?.Report(new("RTX: updating WineGDK for automatic NGX driver discovery"));
                wine = await runtimes.EnsureAsync(RuntimeDefinition.WineGdk, progress, cancellationToken, checkForUpdates: true);
                if (!RtxRuntimeEnvironment.SupportsNgxDiscovery(wine.Tag))
                    throw new InvalidOperationException("Automatic RTX/DLSS setup requires WineGDK 11.18-8-winrt or newer. Update runtimes while online and retry.");
            }
            // Let WineGDK discover the host's NGX libraries during its normal startup.
            if (rtxActive && (explicitNgx is not null || ngxDirectory is not null))
                env["NVIDIA_WINE_DLL_DIR"] = explicitNgx ?? ngxDirectory;
            var wineBinary = RuntimeManager.FindExecutable(wine.Directory, "wine");
            env["WINESERVER"] = RuntimeManager.FindExecutable(wine.Directory, "wineserver");
            var diagnosticEnvironment = new Dictionary<string, string?>(env);
            GameSessionDiagnostics.ConfigureEnvironment(diagnosticEnvironment);
            diagnosticEnvironment = RtxRuntimeEnvironment.Apply(rtxActive, ngxDirectory,
                ngxDirectory is not null && RtxRuntimeEnvironment.HasNvidiaDriver(), diagnosticEnvironment,
                message => ProcessLog.Event(journal.Path, "RTX", message));
            var gameEnvironment = MangoHudIntegration.Apply(options,
                InstanceLaunchPlan.Environment(options, diagnosticEnvironment),
                message => ProcessLog.Event(journal.Path, "MangoHud", message));
            var log = journal.Path;
            var completed = false;
            try
            {
                Checkpoint("Graphics environment");
                var executable = Path.GetRelativePath(game, gameExecutable).Replace('/', '\\');
                await using var diagnostics = new GameSessionDiagnostics(paths.Instance(instance.Id), log);
                await diagnostics.EnableContentLogsAsync(cancellationToken);
                ProcessLog.Event(log, "Diagnostics", $"Minecraft {instance.Version}; WineGDK {wine.Tag}; Xodus {xodus.Tag}. Wine, DXVK and VKD3D console output is captured; custom logging environment overrides remain supported.");
                diagnostics.Start();
                Checkpoint("Game diagnostics");
                progress?.Report(new($"Playing {instance.Name}"));
                var command = InstanceLaunchPlan.Create(RuntimeManager.FindExecutable(xodus.Directory, "xodus-cli"),
                    game, wineBinary, executable, Path.GetDirectoryName(gameExecutable)!, options, gameEnvironment);
                await runner.RunAsync(command, log, cancellationToken, diagnostics.ObserveOutput);
                await runner.RunAsync(new(env["WINESERVER"]!, ["-w"], game, env), log, cancellationToken);
                await diagnostics.DisposeAsync();
                if (diagnostics.HasUnhandledException)
                    throw new IOException($"Wine reported an unhandled exception during the game session, even though the launch process returned success. See {log}");
                completed = true;
                journal.Report(new("Game session finished"));
            }
            finally
            {
                // Only this instance's private Wine server is stopped, including children detached by Wine.
                if (!completed) await WineServer.StopAsync(runner, env["WINESERVER"]!, game, env, log);
            }
        }
        catch (Exception error) { journal.Error(error); throw; }
        finally { running.TryRemove(instance.Id, out _); }
    }
}

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
    Orion.Infrastructure.Rtx.RtxService rtx, XodusSessions sessions, OrionOptimization? optimization = null, RollingGameUpdate? updates = null) : IGameLauncher
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
        var verbose = Environment.GetEnvironmentVariable("ORION_DIAGNOSTICS") == "1";
        var preparation = Stopwatch.StartNew();
        var previous = 0L;
        void Checkpoint(string stage)
        {
            var elapsed = preparation.ElapsedMilliseconds;
            if (verbose) ProcessLog.Event(journal.Path, "Launch timing", $"{stage}: {elapsed - previous} ms; total {elapsed} ms");
            previous = elapsed;
            cancellationToken.ThrowIfCancellationRequested();
        }
        try
        {

            progress?.Report(new("Preparing game launch"));
            cancellationToken.ThrowIfCancellationRequested();
            content.Recover(instance.Id);
            if (updates is not null) instance = await updates.PrepareAsync(instance, progress, cancellationToken);
            Checkpoint("Content recovery");
            var options = instance.LaunchOptions ?? new();
            options.ValidateLaunchInputs();
            var game = paths.Game(instance.Id);
            var gameExecutable = GameLayout.ValidatedExecutable(game);
            await GameConfiguration.EnsureAsync(game, gameExecutable, instance.Channel, cancellationToken);
            File.Delete(Path.Combine(paths.Instance(instance.Id), "launch-optimized-wine"));
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
                (stage, duration) => { if (verbose) ProcessLog.Event(journal.Path, "RTX timing", $"{stage}: {duration.TotalMilliseconds:F1} ms"); });
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
                if (verbose) await diagnostics.EnableContentLogsAsync(cancellationToken);
                if (verbose) ProcessLog.Event(log, "Versions", $"Minecraft {instance.Version}; WineGDK {wine.Tag}; Xodus {xodus.Tag}");
                diagnostics.Start();
                Checkpoint("Game diagnostics");
                if (options.OrionOptimizations)
                {
                    if (optimization is null) throw new InvalidOperationException("Orion optimization loader is unavailable.");
                    async Task<byte[]> ReadLegacy(CancellationToken ct)
                    {
                        var temporary = Path.Combine(paths.Instance(instance.Id), ".decrypted-" + Guid.NewGuid().ToString("N") + ".exe");
                        var exportEnvironment = new Dictionary<string, string?>(gameEnvironment)
                        { [OptimizationSourceExport.DestinationVariable] = temporary };
                        // Xodus supplies the decrypted bytes once; Orion then reuses its disk copy.
                        var host = Path.Combine(AppContext.BaseDirectory, "OrionLauncher");
                        if (!File.Exists(host)) throw new IOException("The Orion apphost is required to prepare a legacy encrypted installation.");
                        try
                        {
                            await runner.RunAsync(new(RuntimeManager.FindExecutable(xodus.Directory, "xodus-cli"),
                                XodusGameCommands.Play(game, host, executable), game, exportEnvironment), log, ct);
                            return await File.ReadAllBytesAsync(temporary, ct);
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                    gameExecutable = await optimization.PrepareAsync(instance.Id, gameExecutable, progress, cancellationToken,
                        GameLayout.IsFullyDecrypted(game) ? null : ReadLegacy);
                    executable = Path.GetRelativePath(game, gameExecutable).Replace('/', '\\');
                    gameEnvironment["ORION_OPTIMIZATION_AUTOSTART"] = "1";
                    Checkpoint("Orion optimizations");
                }
                progress?.Report(new($"Playing {instance.Name}"));
                var command = options.OrionOptimizations || GameLayout.IsFullyDecrypted(game)
                    ? InstanceLaunchPlan.CreateDirect(wineBinary, gameExecutable, Path.GetDirectoryName(gameExecutable)!, options, gameEnvironment)
                    : InstanceLaunchPlan.Create(RuntimeManager.FindExecutable(xodus.Directory, "xodus-cli"),
                    game, wineBinary, executable, Path.GetDirectoryName(gameExecutable)!, options, gameEnvironment);
                await runner.RunAsync(command, log, cancellationToken, diagnostics.ObserveOutput, TimeSpan.FromMilliseconds(250));
                // Xodus has observed the game process exit. Stop this private prefix's
                // remaining desktop helpers, then retain wineserver -w for cleanup.
                await WineServer.StopAsync(runner, env["WINESERVER"]!, game, env, log);
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

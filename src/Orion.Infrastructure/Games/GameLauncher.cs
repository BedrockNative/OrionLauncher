using System.Collections.Concurrent;
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
    Orion.Infrastructure.Rtx.RtxService rtx) : IGameLauncher
{
    private readonly ConcurrentDictionary<Guid, byte> running = new();
    public bool IsRunning(Guid id) => running.ContainsKey(id);

    public async Task RunAsync(GameInstance instance, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var activityLease = activity.Acquire(instance.Id);
        content.Recover(instance.Id);
        rtx.ValidateForLaunchUnderLease(instance.Id);
        if (!running.TryAdd(instance.Id, 0)) throw new InvalidOperationException("This instance is already running.");
        using var journal = new InstanceJournal(paths, instance.Id, null, progress);
        progress = journal;
        try
        {
            var options = instance.LaunchOptions ?? new();
            options.ValidateLaunchInputs();
            var game = paths.Game(instance.Id);
            GameLayout.Validate(game);
            var xodus = await runtimes.EnsureAsync(RuntimeDefinition.Xodus, progress, cancellationToken);
            var accountId = await accounts.ResolveForLaunchAsync(xodus, options.AccountId, cancellationToken);
            var sessionEnvironment = new XodusEnvironment(paths, instance.Id, accountId);
            await using var sessionService = new XodusService(paths, sessionEnvironment, runner);
            journal.AttachService(sessionService);
            XodusGameCommands.RequirePurchaseApi(xodus);
            XodusGameCommands.RequireLaunchArgumentsApi(xodus, options);
            var wine = await runtimes.EnsureAsync(RuntimeDefinition.WineGdk, progress, cancellationToken);
            await sessionService.EnsureAsync(xodus, cancellationToken);
            var env = sessionEnvironment.Create(paths.Prefix(instance.Id));
            var rtxActive = rtx.UsesRtxUnderLease(instance.Id);
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
            // WineGDK's wineboot registers NGXCore from this same directory, not a bundled driver.
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
                progress?.Report(new("Preparing the WineGDK prefix"));
                await runner.RunAsync(new(RuntimeManager.FindExecutable(wine.Directory, "wineboot"), ["-u"], game, env), log, cancellationToken);
                await runner.RunAsync(new(env["WINESERVER"]!, ["-w"], game, env), log, cancellationToken);
                // Wine's graphical crash dialog otherwise sends the backtrace to
                // a temporary file that disappears when the dialog is closed.
                await runner.RunAsync(new(wineBinary, ["reg", "add", @"HKCU\Software\Wine\WineDbg", "/v", "ShowCrashDialog", "/t", "REG_DWORD", "/d", "0", "/f"], game, env), log, cancellationToken);
                await runner.RunAsync(new(env["WINESERVER"]!, ["-w"], game, env), log, cancellationToken);
                var executable = Path.GetRelativePath(game, GameLayout.Executable(game)).Replace('/', '\\');
                rtx.PrepareLaunchUnderLease(instance.Id, progress, cancellationToken);
                await using var diagnostics = new GameSessionDiagnostics(paths.Instance(instance.Id), log);
                await diagnostics.EnableContentLogsAsync(cancellationToken);
                ProcessLog.Event(log, "Diagnostics", $"Minecraft {instance.Version}; WineGDK {wine.Tag}; Xodus {xodus.Tag}. Wine backtraces, DXVK and VKD3D output are captured; custom logging environment overrides remain supported.");
                diagnostics.Start();
                progress?.Report(new($"Playing {instance.Name}"));
                var command = InstanceLaunchPlan.Create(RuntimeManager.FindExecutable(xodus.Directory, "xodus-cli"),
                    game, wineBinary, executable, Path.GetDirectoryName(GameLayout.Executable(game))!, options, gameEnvironment);
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

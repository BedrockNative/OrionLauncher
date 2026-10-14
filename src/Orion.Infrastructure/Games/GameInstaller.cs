using Orion.Application;
using Orion.Domain;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Runtime;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Games;

public sealed class GameInstaller(AppPaths paths, RuntimeManager runtimes, ProcessRunner runner,
    XodusEnvironment environment, IXodusService service, ResumableDownload? download = null, XvdPackageExtractor? extractor = null, AccountService? accounts = null) : IGameInstaller, IGameUpdateInstaller
{
    public Task InstallAsync(GameInstance instance, string source, IProgress<OperationProgress>? progress, CancellationToken cancellationToken) =>
        InstallIntoAsync(instance, source, paths.Instance(instance.Id), progress, cancellationToken);

    public Task PrepareUpdateAsync(GameInstance instance, string source, IProgress<OperationProgress>? progress, CancellationToken ct) =>
        InstallIntoAsync(instance, source, Path.Combine(paths.Instance(instance.Id), ".update-next"), progress, ct);

    private async Task InstallIntoAsync(GameInstance instance, string source, string target, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        using var journal = new InstanceJournal(paths, instance.Id, service, progress);
        progress = journal;
        try
        {
            var local = Path.IsPathFullyQualified(source) && File.Exists(source);
            if (!local && (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !GamePackageSource.IsSupported(uri)))
                throw new ArgumentException("Select a supported catalog package or a local .msixvc file.");
            if (local && !source.EndsWith(".msixvc", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only GDK .msixvc packages are supported.");
            var stage = paths.InstallStage(instance.Id);
            if (Directory.Exists(target))
            {
                var committed = await AtomicFile.ReadJsonAsync<GameInstance>(Path.Combine(target, "instance.json"), cancellationToken);
                if (committed?.Id != instance.Id) throw new IOException("Instance already exists.");
                var installedGame = Path.Combine(target, "game");
                await GameConfiguration.EnsureAsync(installedGame, GameLayout.ValidatedExecutable(installedGame), instance.Channel, cancellationToken);
                return;
            }
            var xodus = await runtimes.EnsureAsync(RuntimeDefinition.Xodus, progress, cancellationToken);
            XodusGameCommands.RequirePurchaseApi(xodus);
            await service.EnsureAsync(xodus, cancellationToken);
            var installEnvironment = environment.Create();
            string? account = null;
            if (extractor is not null)
            {
                account = await (accounts ?? throw new InvalidOperationException("Account service unavailable.")).ResolveForLaunchAsync(xodus, instance.LaunchOptions.AccountId, cancellationToken)
                    ?? throw new InvalidOperationException("Sign in before installing Minecraft.");
                installEnvironment["XODUS_ACCOUNT_ID"] = account;
            }
            Directory.CreateDirectory(stage);
            // Verify before requesting any game bytes, including when resuming.
            progress?.Report(new("Verifying a Minecraft purchase through isolated Xodus"));
            await runner.CaptureAsync(new(RuntimeManager.FindExecutable(xodus.Directory, "xodus-cli"),
                ["check-ownership", XodusGameCommands.MinecraftWindowsProduct], stage, installEnvironment), cancellationToken,
                "Minecraft purchase could not be verified through Xodus. Check your accounts and connection; no game data was downloaded.");
            if (!local && download is not null)
            {
                var package = Path.Combine(paths.Download(instance.Id), "package.msixvc");
                await download.DownloadAsync(new Uri(source), package, progress, cancellationToken);
                source = package; local = true;
            }
            // Partial extraction is never published. Rebuild it from the retained encrypted package.
            var destination = Path.Combine(stage, "game");
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            progress?.Report(new("Extracting and validating the instance"));
            if (extractor is not null)
            {
                if (!local) throw new InvalidOperationException("Full extraction requires a downloaded package.");
                await extractor.ExtractAsync(source, destination, RuntimeManager.FindExecutable(xodus.Directory, "xodus-cli"),
                    account!, installEnvironment, progress, cancellationToken);
            }
            else
                await runner.RunAsync(new(RuntimeManager.FindExecutable(xodus.Directory, "xodus-cli"),
                    XodusGameCommands.Install(source, destination, local), stage, installEnvironment), journal.Path, cancellationToken);
            await GameConfiguration.EnsureAsync(destination, GameLayout.ValidatedExecutable(destination), instance.Channel, cancellationToken);
            await AtomicFile.WriteJsonAsync(Path.Combine(stage, "instance.json"), instance, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stage, target);
            journal.Report(new("Installation finished"));
            // The durable queue owns cleanup. Interruption keeps the package for resumption.
        }
        catch (Exception error) { journal.Error(error); throw; }
    }
}

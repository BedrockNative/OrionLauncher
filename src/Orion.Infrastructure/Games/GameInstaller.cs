using Orion.Application;
using Orion.Domain;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Runtime;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Games;

public sealed class GameInstaller(AppPaths paths, RuntimeManager runtimes, ProcessRunner runner,
    XodusEnvironment environment, IXodusService service, ResumableDownload? download = null) : IGameInstaller
{
    public async Task InstallAsync(GameInstance instance, string source, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
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
            if (Directory.Exists(paths.Instance(instance.Id)))
            {
                var committed = await AtomicFile.ReadJsonAsync<GameInstance>(Path.Combine(paths.Instance(instance.Id), "instance.json"), cancellationToken);
                if (committed?.Id != instance.Id) throw new IOException("Instance already exists.");
                GameLayout.Validate(paths.Game(instance.Id));
                return;
            }
            var xodus = await runtimes.EnsureAsync(RuntimeDefinition.Xodus, progress, cancellationToken);
            XodusGameCommands.RequirePurchaseApi(xodus);
            await service.EnsureAsync(xodus, cancellationToken);
            Directory.CreateDirectory(stage);
            // Verify before requesting any game bytes, including when resuming.
            progress?.Report(new("Verifying a Minecraft purchase through isolated Xodus"));
            await runner.CaptureAsync(new(RuntimeManager.FindExecutable(xodus.Directory, "xodus-cli"),
                ["check-ownership", XodusGameCommands.MinecraftWindowsProduct], stage, environment.Create()), cancellationToken,
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
            await runner.RunAsync(new(RuntimeManager.FindExecutable(xodus.Directory, "xodus-cli"),
                XodusGameCommands.Install(source, destination, local), stage, environment.Create()),
                journal.Path, cancellationToken);
            GameLayout.Validate(destination);
            await AtomicFile.WriteJsonAsync(Path.Combine(stage, "instance.json"), instance, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stage, paths.Instance(instance.Id));
            journal.Report(new("Installation finished"));
            // The durable queue owns cleanup. Interruption keeps the package for resumption.
        }
        catch (Exception error) { journal.Error(error); throw; }
    }
}

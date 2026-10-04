using Orion.Domain;

namespace Orion.Application;

public interface IInstanceActivity
{
    IDisposable Acquire(Guid id);
}

public interface IReleaseClient
{
    Task<Release> GetLatestAsync(Repository repository, CancellationToken cancellationToken = default);
}

public interface IInstanceRepository
{
    Task<IReadOnlyList<GameInstance>> ListAsync(CancellationToken cancellationToken = default);
    Task<GameInstance> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(GameInstance instance, CancellationToken cancellationToken = default);
    Task ArchiveAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IVersionCatalog
{
    Task<IReadOnlyList<GameVersion>> GetAsync(CancellationToken cancellationToken = default);
}

public interface IGameInstaller
{
    Task InstallAsync(GameInstance instance, string source, IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IGameLauncher
{
    bool IsRunning(Guid id);
    Task RunAsync(GameInstance instance, IProgress<OperationProgress>? progress, CancellationToken cancellationToken);
}

public interface IDesktopIntegration
{
    Task SynchronizeAsync(IReadOnlyList<GameInstance> instances, CancellationToken cancellationToken = default);
}

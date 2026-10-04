using Orion.Domain;

namespace Orion.Application;

/// <summary>Coordinates instance use cases without knowing UI, HTTP or Wine details.</summary>
public sealed class InstanceService(IInstanceRepository repository, IGameInstaller installer,
    IGameLauncher launcher, IDesktopIntegration desktop, IInstanceActivity activity)
{
    public Task<IReadOnlyList<GameInstance>> ListAsync(CancellationToken ct = default) => repository.ListAsync(ct);

    public async Task<GameInstance> CreateAsync(string name, string version, string channel, string source,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var instance = GameInstance.Create(name, version, channel);
        return await CreateAsync(instance, source, progress, ct);
    }

    public async Task<GameInstance> CreateAsync(GameInstance instance, string source,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        await installer.InstallAsync(instance, source, progress, ct);
        // Once installation has committed, finish registration even if cancellation arrives now.
        await repository.SaveAsync(instance, CancellationToken.None);
        await SynchronizeAsync(CancellationToken.None);
        return instance;
    }

    public async Task RenameAsync(Guid id, string name, CancellationToken ct = default)
    {
        using var lease = activity.Acquire(id);
        var instance = await repository.GetAsync(id, ct);
        await repository.SaveAsync(instance with { Name = GameInstance.ValidateName(name) }, ct);
        await SynchronizeAsync(ct);
    }

    public async Task ArchiveAsync(Guid id, CancellationToken ct = default)
    {
        using var lease = activity.Acquire(id);
        if (launcher.IsRunning(id)) throw new InvalidOperationException("Stop the game before archiving its instance.");
        await repository.ArchiveAsync(id, ct);
        await SynchronizeAsync(ct);
    }

    public async Task SetLaunchOptionsAsync(Guid id, InstanceLaunchOptions options, CancellationToken ct = default)
    {
        using var lease = activity.Acquire(id);
        if (launcher.IsRunning(id)) throw new InvalidOperationException("Stop the game before changing its launch options.");
        options.Validate();
        var instance = await repository.GetAsync(id, ct);
        await repository.SaveAsync(instance with { LaunchOptions = options }, ct);
    }

    public async Task PlayAsync(Guid id, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var instance = await repository.GetAsync(id, ct);
        await launcher.RunAsync(instance, progress, ct);
        var latest = await repository.GetAsync(id, ct);
        await repository.SaveAsync(latest with { LastPlayedAt = DateTimeOffset.UtcNow }, ct);
    }

    public async Task SetConfigurationAsync(Guid id, string name, InstanceLaunchOptions options, string? coverId,
        bool desktopShortcut, CancellationToken ct = default)
    {
        using var lease = activity.Acquire(id);
        if (launcher.IsRunning(id)) throw new InvalidOperationException("Stop the game before changing its configuration.");
        options.Validate(); GameInstance.ValidateCoverId(coverId);
        name = GameInstance.ValidateName(name);
        var instance = await repository.GetAsync(id, ct);
        await repository.SaveAsync(instance with { Name = name, LaunchOptions = options, CoverId = coverId, DesktopShortcut = desktopShortcut }, ct);
        await SynchronizeAsync(ct);
    }

    public async Task SynchronizeAsync(CancellationToken ct = default) =>
        await desktop.SynchronizeAsync(await repository.ListAsync(ct), ct);
}

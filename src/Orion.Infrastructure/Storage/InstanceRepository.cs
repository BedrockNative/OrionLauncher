using Orion.Application;
using Orion.Domain;

namespace Orion.Infrastructure.Storage;

public sealed class InstanceRepository(AppPaths paths) : IInstanceRepository
{
    public async Task<IReadOnlyList<GameInstance>> ListAsync(CancellationToken cancellationToken = default)
    {
        var instances = new List<GameInstance>();
        if (!Directory.Exists(paths.Instances)) return instances;
        foreach (var folder in Directory.EnumerateDirectories(paths.Instances))
        {
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out var id)) continue;
            var instance = await AtomicFile.ReadJsonAsync<GameInstance>(Path.Combine(folder, "instance.json"), cancellationToken);
            if (instance is null) continue;
            if (instance.Id != id) throw new InvalidDataException($"Instance identity mismatch: {folder}");
            instances.Add(instance);
        }
        return instances.OrderByDescending(i => i.LastPlayedAt ?? i.CreatedAt).ToArray();
    }

    public async Task<GameInstance> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = await AtomicFile.ReadJsonAsync<GameInstance>(Manifest(id), cancellationToken)
            ?? throw new FileNotFoundException("Instance not found.", Manifest(id));
        if (instance.Id != id) throw new InvalidDataException("Instance identity mismatch.");
        return instance;
    }

    public Task SaveAsync(GameInstance instance, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteJsonAsync(Manifest(instance.Id), instance, cancellationToken);

    public Task ArchiveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(paths.Archives);
        Directory.Move(paths.Instance(id), Path.Combine(paths.Archives, $"{id:N}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"));
        return Task.CompletedTask;
    }
    private string Manifest(Guid id) => Path.Combine(paths.Instance(id), "instance.json");
}

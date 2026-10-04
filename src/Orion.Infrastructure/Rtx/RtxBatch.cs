using Orion.Domain;
using Orion.Infrastructure.Content;

namespace Orion.Infrastructure.Rtx;

public sealed record RtxBatchResult(Guid InstanceId, string Name, bool Success, string? Error);

public static class RtxBatch
{
    // Atomic per instance, not across the batch. Completed instances remain successful
    // on cancellation; callers always receive the outcomes, including skipped targets.
    public static async Task<IReadOnlyList<RtxBatchResult>> RunAsync(IReadOnlyList<GameInstance> instances,
        Func<GameInstance, CancellationToken, Task> action, CancellationToken ct)
    {
        var results = new List<RtxBatchResult>();
        foreach (var instance in instances.DistinctBy(i => i.Id))
        {
            if (ct.IsCancellationRequested) { results.Add(new(instance.Id, instance.Name, false, "Cancelled; not started.")); continue; }
            try { await action(instance, ct); results.Add(new(instance.Id, instance.Name, true, null)); }
            catch (OperationCanceledException) { results.Add(new(instance.Id, instance.Name, false, "Cancelled.")); }
            catch (Exception ex) { results.Add(new(instance.Id, instance.Name, false, ex.Message)); }
        }
        return results;
    }
}

public sealed partial class RtxService
{
    public async Task<IReadOnlyList<RtxBatchResult>> InstallLatestDlssAsync(IReadOnlyList<GameInstance> instances, CancellationToken ct)
    {
        var work = ContentFiles.Safe(paths.Cache, "rtx/dlss-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        try
        {
            var file = Path.Combine(work, "nvngx_dlss.dll"); var source = await catalog.DownloadDlssAsync(file, ct);
            return await RtxBatch.RunAsync(instances, (instance, token) => InstallDlssAsync(instance.Id, file, source, token), ct);
        }
        finally { if (Directory.Exists(work)) ContentFiles.DeleteWork(work); }
    }
}

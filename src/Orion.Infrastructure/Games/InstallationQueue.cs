using Orion.Domain;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Games;

public enum DownloadState { Queued, Running, Failed, Cancelling, Completed, Cancelled }
public sealed record InstallationJob(GameInstance Instance, string Source, DownloadState State = DownloadState.Queued,
    string Message = "", double? Fraction = null)
{
    public Guid Id => Instance.Id;
    public bool IsPending => State is DownloadState.Queued or DownloadState.Running or DownloadState.Cancelling;
}

/// <summary>One durable worker; completed history is session-only. Shutdown pauses, explicit cancel discards.</summary>
public sealed class InstallationQueue(AppPaths paths,
    Func<GameInstance, string, IProgress<OperationProgress>, CancellationToken, Task> install) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1), wake = new(0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<Guid, InstallationJob> jobs = [];
    private CancellationTokenSource? current;
    private Guid? currentId;
    private Task? worker;
    public event Action<InstallationJob>? Changed;
    public IReadOnlyList<InstallationJob> Snapshot { get { lock (jobs) return jobs.Values.OrderByDescending(j => j.Instance.CreatedAt).ToArray(); } }
    public bool HasPending => Snapshot.Any(j => j.IsPending);
    private string Manifest(Guid id) => Path.Combine(paths.Download(id), "job.json");
    private void Publish(InstallationJob job) { lock (jobs) jobs[job.Id] = job; Changed?.Invoke(job); }
    private Task Save(InstallationJob job) => AtomicFile.WriteJsonAsync(Manifest(job.Id), job);

    public async Task StartAsync()
    {
        if (worker is not null) return;
        Directory.CreateDirectory(paths.Downloads);
        foreach (var directory in Directory.EnumerateDirectories(paths.Downloads))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) continue;
            var job = await AtomicFile.ReadJsonAsync<InstallationJob>(Manifest(id));
            if (job is null) continue;
            if (job.Id != id || !Uri.TryCreate(job.Source, UriKind.Absolute, out var source) || !GamePackageSource.IsSupported(source))
                throw new InvalidDataException("Invalid saved installation job.");
            Publish(job with { State = job.State == DownloadState.Cancelling ? DownloadState.Cancelling : DownloadState.Queued, Message = "Resuming interrupted installation", Fraction = null });
        }
        worker = Task.Run(WorkAsync);
        wake.Release();
    }

    public async Task EnqueueAsync(GameInstance instance, string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !GamePackageSource.IsSupported(uri))
            throw new ArgumentException("Choose a supported catalog download.");
        await gate.WaitAsync();
        try
        {
            var job = new InstallationJob(instance, source);
            if (Snapshot.Any(j => j.Id == job.Id)) throw new InvalidOperationException("Installation already queued.");
            await Save(job); Publish(job); wake.Release();
        }
        finally { gate.Release(); }
    }

    public async Task CancelAsync(Guid id)
    {
        await gate.WaitAsync();
        try
        {
            var job = Snapshot.Single(j => j.Id == id);
            if (job.State is DownloadState.Completed or DownloadState.Cancelled) return;
            job = job with { State = DownloadState.Cancelling, Message = "Cancelling and removing temporary files" };
            await Save(job); Publish(job); // durable intent survives even a crash during cleanup
            if (currentId == id) current?.Cancel();
            wake.Release();
        }
        finally { gate.Release(); }
    }

    public async Task RetryAsync(Guid id)
    {
        await gate.WaitAsync();
        try
        {
            var job = Snapshot.Single(j => j.Id == id);
            if (job.State != DownloadState.Failed) return;
            job = job with { State = DownloadState.Queued, Message = "", Fraction = null };
            await Save(job); Publish(job); wake.Release();
        }
        finally { gate.Release(); }
    }

    private async Task WorkAsync()
    {
        try
        {
            while (true)
            {
                await wake.WaitAsync(lifetime.Token);
                while (!lifetime.IsCancellationRequested)
                {
                    InstallationJob? job;
                    await gate.WaitAsync();
                    try
                    {
                        job = Snapshot.OrderBy(j => j.Instance.CreatedAt).FirstOrDefault(j => j.State is DownloadState.Queued or DownloadState.Cancelling);
                        if (job is null) break;
                        currentId = job.Id;
                        current = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        if (job.State != DownloadState.Cancelling) Publish(job = job with { State = DownloadState.Running });
                    }
                    finally { gate.Release(); }
                    Exception? failure = null; bool committed = false;
                    try
                    {
                        if (job.State != DownloadState.Cancelling)
                        {
                            await install(job.Instance, job.Source, new CallbackProgress(p =>
                            {
                                InstallationJob latest;
                                lock (jobs)
                                {
                                    latest = jobs[job.Id];
                                    if (latest.State != DownloadState.Running) return;
                                    jobs[job.Id] = latest = latest with { Message = p.Message, Fraction = p.Fraction };
                                }
                                Changed?.Invoke(latest);
                            }), current.Token);
                            committed = true;
                        }
                    }
                    catch (Exception error) { failure = error; }
                    await gate.WaitAsync();
                    try
                    {
                        var latest = Snapshot.Single(j => j.Id == job.Id);
                        if (committed || latest.State == DownloadState.Cancelling)
                        {
                            Cleanup(job.Id);
                            Publish(latest with { State = committed ? DownloadState.Completed : DownloadState.Cancelled, Message = "", Fraction = committed ? 1 : null });
                        }
                        else
                        {
                            latest = latest with
                            {
                                State = lifetime.IsCancellationRequested ? DownloadState.Queued : DownloadState.Failed,
                                Message = failure?.Message ?? "Installation interrupted",
                                Fraction = null
                            };
                            await Save(latest); Publish(latest);
                        }
                    }
                    catch (Exception error) { Publish(job with { State = DownloadState.Failed, Message = error.Message }); }
                    finally { current.Dispose(); current = null; currentId = null; gate.Release(); }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private void Cleanup(Guid id)
    {
        // Only this job's staging/cache. Never remove committed instances or shared runtimes.
        foreach (var path in new[] { paths.InstallStage(id), paths.Download(id) })
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    public async Task PauseAsync()
    {
        await lifetime.CancelAsync();
        if (worker is not null) await worker;
    }
    public async ValueTask DisposeAsync() { await PauseAsync(); lifetime.Dispose(); gate.Dispose(); wake.Dispose(); }
    private sealed class CallbackProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    { public void Report(OperationProgress value) => report(value); }
}

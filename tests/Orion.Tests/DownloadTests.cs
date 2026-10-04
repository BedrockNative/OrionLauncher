using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Orion.Domain;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

public sealed class DownloadTests
{
    private static readonly Uri Source = new("https://example.test/game.msixvc");
    private static async Task Seed(string path, string data = "abc", string validator = "\"one\"")
    {
        await File.WriteAllTextAsync(path, data);
        await AtomicFile.WriteJsonAsync(path + ".json", new { source = Source.AbsoluteUri, validator, length = 6 });
    }

    [Fact]
    public async Task ResumesValidatedBytesWithRangeAndIfRange()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "package"); await Seed(path);
        using var http = new HttpClient(new ReleaseTests.Handler(request =>
        {
            Assert.Equal("bytes=3-", request.Headers.Range!.ToString());
            Assert.Equal("\"one\"", request.Headers.GetValues("If-Range").Single());
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StringContent("def") };
            response.Headers.ETag = new("\"one\""); response.Content.Headers.ContentRange = new(3, 5, 6);
            return response;
        }));
        await new ResumableDownload(http).DownloadAsync(Source, path, null, default);
        Assert.Equal("abcdef", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ServerIgnoringRangeRestartsWithoutMixingVersions()
    {
        using var directory = new TestDirectory(); var path = Path.Combine(directory.Root, "package"); await Seed(path);
        using var http = new HttpClient(new ReleaseTests.Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("new") }));
        await new ResumableDownload(http).DownloadAsync(Source, path, null, default);
        Assert.Equal("new", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(2, "\"one\"")]
    [InlineData(3, "\"changed\"")]
    public async Task RejectsInconsistentRangesWithoutAppending(int start, string validator)
    {
        using var directory = new TestDirectory(); var path = Path.Combine(directory.Root, "package"); await Seed(path);
        using var http = new HttpClient(new ReleaseTests.Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StringContent("def") };
            response.Headers.ETag = new(validator); response.Content.Headers.ContentRange = new(start, 5, 6); return response;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ResumableDownload(http).DownloadAsync(Source, path, null, default));
        Assert.Equal("abc", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task TruncatedResponsePersistsBytesForNextAttempt()
    {
        using var directory = new TestDirectory(); var path = Path.Combine(directory.Root, "package");
        using var http = new HttpClient(new ReleaseTests.Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("abc") };
            response.Content.Headers.ContentLength = 6; response.Headers.ETag = new("\"one\""); return response;
        }));
        await Assert.ThrowsAsync<IOException>(() => new ResumableDownload(http).DownloadAsync(Source, path, null, default));
        Assert.Equal("abc", await File.ReadAllTextAsync(path));
        Assert.Contains("one", await File.ReadAllTextAsync(path + ".json"));
    }

    [Fact]
    public async Task ExplicitCancelWaitsForWorkerThenRemovesOnlyOwnedTemporaryFiles()
    {
        using var directory = new TestDirectory();
        var instance = GameInstance.Create("Download", "1", "Release");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var other = GameInstance.Create("Keep", "1", "Release");
        Directory.CreateDirectory(directory.Paths.Instance(other.Id));
        await using var queue = new InstallationQueue(directory.Paths, async (job, _, _, ct) =>
        {
            Directory.CreateDirectory(directory.Paths.InstallStage(job.Id));
            await File.WriteAllTextAsync(Path.Combine(directory.Paths.Download(job.Id), "partial"), "data", ct);
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } finally { stopped = true; }
        });
        await queue.StartAsync(); await queue.EnqueueAsync(instance, Source.AbsoluteUri);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.CancelAsync(instance.Id);
        await Until(() => queue.Snapshot.Single().State == DownloadState.Cancelled);
        Assert.True(stopped);
        Assert.False(Directory.Exists(directory.Paths.Download(instance.Id)));
        Assert.False(Directory.Exists(directory.Paths.InstallStage(instance.Id)));
        Assert.True(Directory.Exists(directory.Paths.Instance(other.Id)));
    }

    [Fact]
    public async Task ClosingPausesAndNextSessionRecoversSameIdentityAndBytes()
    {
        using var directory = new TestDirectory(); var instance = GameInstance.Create("Resume", "1", "Release");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var queue = new InstallationQueue(directory.Paths, async (job, _, _, ct) =>
        {
            await File.WriteAllTextAsync(Path.Combine(directory.Paths.Download(job.Id), "package.part"), "saved", ct);
            entered.SetResult(); await Task.Delay(Timeout.Infinite, ct);
        }))
        {
            await queue.StartAsync(); await queue.EnqueueAsync(instance, Source.AbsoluteUri);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await queue.PauseAsync();
            Assert.Equal(DownloadState.Queued, queue.Snapshot.Single().State);
        }
        Guid? recovered = null;
        await using var next = new InstallationQueue(directory.Paths, async (job, _, _, _) =>
        {
            recovered = job.Id;
            Assert.Equal("saved", await File.ReadAllTextAsync(Path.Combine(directory.Paths.Download(job.Id), "package.part")));
        });
        await next.StartAsync(); await Until(() => next.Snapshot.Single().State == DownloadState.Completed);
        Assert.Equal(instance.Id, recovered);
        Assert.False(Directory.Exists(directory.Paths.Download(instance.Id)));
    }

    [Fact]
    public async Task CrashDuringCancellationFinishesCleanupWithoutRestartingInstall()
    {
        using var directory = new TestDirectory(); var instance = GameInstance.Create("Cancel", "1", "Release");
        var job = new InstallationJob(instance, Source.AbsoluteUri, DownloadState.Cancelling);
        await AtomicFile.WriteJsonAsync(Path.Combine(directory.Paths.Download(instance.Id), "job.json"), job);
        Directory.CreateDirectory(directory.Paths.InstallStage(instance.Id));
        await using var queue = new InstallationQueue(directory.Paths, (_, _, _, _) => throw new InvalidOperationException("Must not install"));
        await queue.StartAsync(); await Until(() => queue.Snapshot.Single().State == DownloadState.Cancelled);
        Assert.False(Directory.Exists(directory.Paths.Download(instance.Id)));
        Assert.False(Directory.Exists(directory.Paths.InstallStage(instance.Id)));
    }

    [Fact]
    public async Task FailureCanBeRetriedAndCompletedHistoryIsSessionOnly()
    {
        using var directory = new TestDirectory(); var instance = GameInstance.Create("Retry", "1", "Release"); int attempts = 0;
        await using (var queue = new InstallationQueue(directory.Paths, (_, _, _, _) => ++attempts == 1 ? Task.FromException(new IOException("offline")) : Task.CompletedTask))
        {
            await queue.StartAsync(); await queue.EnqueueAsync(instance, Source.AbsoluteUri);
            await Until(() => queue.Snapshot.Single().State == DownloadState.Failed);
            await queue.RetryAsync(instance.Id);
            await Until(() => queue.Snapshot.Single().State == DownloadState.Completed);
            Assert.Single(queue.Snapshot);
        }
        await using var next = new InstallationQueue(directory.Paths, (_, _, _, _) => Task.CompletedTask);
        await next.StartAsync(); Assert.Empty(next.Snapshot);
    }

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }
}

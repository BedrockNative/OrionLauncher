using System.Collections.Concurrent;
using System.IO.Pipes;
using Orion.Infrastructure.Linux;

namespace Orion.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public async Task RtxLinksAndLongLocalArchivePathsAreForwardedIntact()
    {
        using var directory = new TestDirectory();
        await using var primary = await SingleInstance.AcquireAsync(directory.Paths, new());
        var received = new TaskCompletionSource<LaunchRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary!.Listen(request => received.TrySetResult(request));
        var path = "/tmp/" + string.Join('/', Enumerable.Repeat(new string('a', 80), 8)) + "/preset.rtpack";
        var request = new LaunchRequest(RtxFile: path);
        Assert.Null(await SingleInstance.AcquireAsync(directory.Paths, request));
        Assert.Equal(request, await received.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }
    [Fact]
    public async Task ListenerAndSynchronousShutdownDoNotNeedTheStoppedUiDispatcher()
    {
        using var directory = new TestDirectory();
        var dispatcher = new StoppedDispatcher();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(dispatcher);
            try
            {
                var primary = SingleInstance.AcquireAsync(directory.Paths, new()).GetAwaiter().GetResult()!;
                try
                {
                    var received = new TaskCompletionSource<LaunchRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
                    primary.Listen(request => received.TrySetResult(request));
                    Assert.Throws<InvalidOperationException>(() => primary.Listen(_ => { }));
                    var request = new LaunchRequest(Guid.NewGuid(), true);
                    Assert.Null(SingleInstance.AcquireAsync(directory.Paths, request, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult());
                    Assert.Equal(request, received.Task.GetAwaiter().GetResult());
                }
                finally
                {
                    primary.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    primary.DisposeAsync().AsTask().GetAwaiter().GetResult(); // Program's fallback cleanup.
                }
                completed.TrySetResult();
            }
            catch (Exception error) { completed.TrySetException(error); }
        }) { IsBackground = true };
        thread.Start();
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally
        {
            // If the regression returns, release captured continuations so the test
            // reports a failure without leaving its thread and lock behind.
            for (var i = 0; i < 100 && thread.IsAlive; i++) { dispatcher.Drain(); await Task.Delay(10); }
        }
        Assert.Equal(0, dispatcher.Posts);
        await using var reopened = await SingleInstance.AcquireAsync(directory.Paths, new());
        Assert.NotNull(reopened);
    }

    [Fact]
    public async Task UnresponsiveOwnerReportsActionableErrorWithoutStealingItsLock()
    {
        using var directory = new TestDirectory();
        await using var primary = await SingleInstance.AcquireAsync(directory.Paths, new());
        await using var server = new NamedPipeServerStream($"orion-{directory.Paths.Identity}", PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var connected = server.WaitForConnectionAsync(deadline.Token);
        var secondary = SingleInstance.AcquireAsync(directory.Paths, new(), TimeSpan.FromMilliseconds(300));
        await connected;
        using var reader = new StreamReader(server, leaveOpen: true);
        Assert.NotNull(await reader.ReadLineAsync(deadline.Token)); // Reproduce timeout waiting for the acknowledgement.
        var error = await Assert.ThrowsAsync<LauncherUnresponsiveException>(() => secondary);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Contains("tray menu", error.Message);
        Assert.True(File.Exists(Path.Combine(directory.Paths.Config, "launcher.lock")));
        Assert.Throws<IOException>(() => new FileStream(Path.Combine(directory.Paths.Config, "launcher.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
    }

    [Fact]
    public async Task OwnerExitingDuringHandoffAllowsReacquisitionWithoutDeletingTheLock()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Paths.Config, "launcher.lock");
        await File.WriteAllTextAsync(path, "same lock file");
        await using var primary = await SingleInstance.AcquireAsync(directory.Paths, new());
        await using var server = new NamedPipeServerStream($"orion-{directory.Paths.Identity}", PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var connected = server.WaitForConnectionAsync(deadline.Token);
        var handoff = SingleInstance.AcquireAsync(directory.Paths, new(), TimeSpan.FromMilliseconds(300));
        await connected;
        await primary!.DisposeAsync();
        await using (var replacement = await handoff) Assert.NotNull(replacement);
        Assert.Equal("same lock file", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DisconnectAndMalformedRequestDoNotBreakTheNextInvocation()
    {
        using var directory = new TestDirectory();
        await using var primary = await SingleInstance.AcquireAsync(directory.Paths, new());
        primary!.Listen(_ => { });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using (var client = new NamedPipeClientStream(".", $"orion-{directory.Paths.Identity}", PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(deadline.Token);
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("not json");
            using var reader = new StreamReader(client, leaveOpen: true);
            Assert.Null(await reader.ReadLineAsync(deadline.Token));
        }
        Assert.Null(await SingleInstance.AcquireAsync(directory.Paths, new(), TimeSpan.FromSeconds(2)));
    }

    private sealed class StoppedDispatcher : SynchronizationContext
    {
        private readonly ConcurrentQueue<Action> pending = new();
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        { Interlocked.Increment(ref Posts); pending.Enqueue(() => callback(state)); }
        public void Drain() { while (pending.TryDequeue(out var callback)) callback(); }
    }
}

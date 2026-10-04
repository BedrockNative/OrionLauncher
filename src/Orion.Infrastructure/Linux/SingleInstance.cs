using System.IO.Pipes;
using System.Text.Json;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Linux;

public sealed record LaunchRequest(Guid? Instance = null, bool Background = false, string? RtxLink = null, string? RtxFile = null);

public sealed class LauncherUnresponsiveException(Exception inner) : IOException(
    "Another Orion process is still running but did not respond. Close Orion from its tray menu and try again. " +
    "If it is stuck shutting down, end that Orion process first. Do not delete launcher.lock while it is running.", inner);

public sealed class SingleInstance : IAsyncDisposable
{
    private readonly FileStream ownership;
    private readonly string pipeName;
    private readonly CancellationTokenSource lifetime = new();
    private Task? listener;
    private Task? disposal;
    private readonly object state = new();

    private SingleInstance(FileStream ownership, string pipeName) { this.ownership = ownership; this.pipeName = pipeName; }

    public static async Task<SingleInstance?> AcquireAsync(AppPaths paths, LaunchRequest request, TimeSpan? responseTimeout = null)
    {
        var pipe = $"orion-{paths.Identity}";
        SingleInstance Own()
        {
            var file = new FileStream(Path.Combine(paths.Config, "launcher.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            return new(file, pipe);
        }
        try { return Own(); }
        catch (IOException)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var timeout = new CancellationTokenSource(responseTimeout ?? TimeSpan.FromSeconds(15));
                await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(client, leaveOpen: true);
                await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token).ConfigureAwait(false);
                if (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) != "OK") throw new IOException("The running launcher did not accept this request.");
                return null;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException)
            {
                // The primary may have exited while we connected. Re-acquire only
                // through the OS lock: never unlink it or steal a live owner's socket.
                try { return Own(); }
                catch (IOException) { throw new LauncherUnresponsiveException(error); }
            }
        }
    }

    public void Listen(Action<LaunchRequest> received)
    {
        lock (state)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (listener is not null) throw new InvalidOperationException("The launcher listener is already running.");
            // IPC must keep working independently of the UI dispatcher, including
            // while it is stopping. The recipient explicitly posts UI work itself.
            listener = Task.Run(() => ListenAsync(received, lifetime.Token));
        }
    }

    private async Task ListenAsync(Action<LaunchRequest> received, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var reader = new StreamReader(server, leaveOpen: true);
                using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is { Length: < 16384 } && JsonSerializer.Deserialize<LaunchRequest>(line) is { } request)
                { received(request); await writer.WriteLineAsync("OK".AsMemory(), timeout.Token).ConfigureAwait(false); }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (JsonException) { }
            catch (IOException) { }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (state) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            if (listener is not null)
                try { await listener.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        finally { ownership.Dispose(); lifetime.Dispose(); }
    }
}

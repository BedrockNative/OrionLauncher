using System.Diagnostics;
using System.Net.Sockets;
using Orion.Domain;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Runtime;

/// <summary>Owns only the launcher-created service process. Never attaches to or kills system Xodus.</summary>
public sealed class XodusService(AppPaths paths, XodusEnvironment environment, ProcessRunner? runner = null) : IXodusService, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private Task? observation;
    private CancellationTokenSource? lifetime;
    private string? serviceExecutable;
    public event Action<string>? OutputReceived;

    // Startup can be requested by the UI before Play. Keep process creation and
    // the service's long-lived output observer off the dispatcher as well.
    public Task EnsureAsync(RuntimeInstallation installation, CancellationToken ct) =>
        Task.Run(() => EnsureCoreAsync(installation, ct), ct);

    private async Task EnsureCoreAsync(RuntimeInstallation installation, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var executable = RuntimeManager.FindExecutable(installation.Directory, "xodus-service");
            if (process is { HasExited: false } && serviceExecutable == executable && await IsListeningAsync(ct)) return;
            await StopAsync();
            // Refuse to claim an existing endpoint: ownership comes from our child process, not a socket filename.
            if (await IsListeningAsync(ct)) throw new IOException("Orion's private Xodus socket is already in use.");
            if (File.Exists(environment.SocketPath)) File.Delete(environment.SocketPath);
            lifetime = new CancellationTokenSource();
            process = Process.Start((runner ?? new ProcessRunner()).OwnedStartInfo(new(
                executable, [], paths.XodusProfile, environment.Create())))
                ?? throw new IOException("Could not start isolated Xodus.");
            serviceExecutable = executable;
            observation = ProcessRunner.ObserveAsync(process, Path.Combine(paths.Logs, "xodus-service.log"), lifetime.Token,
                text => OutputReceived?.Invoke(text));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Secret Service allows up to five minutes for its native password
            // prompt. Do not kill Xodus while the user is still unlocking it.
            timeout.CancelAfter(TimeSpan.FromMinutes(6));
            try
            {
                while (!await IsListeningAsync(timeout.Token))
                {
                    if (process.HasExited)
                    {
                        await observation;
                        throw new IOException("Xodus stopped before its socket became available.");
                    }
                    await Task.Delay(100, timeout.Token);
                }
            }
            catch { await StopAsync(); throw; }
        }
        finally { gate.Release(); }
    }

    private async Task<bool> IsListeningAsync(CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(environment.SocketPath), ct); return true; }
        catch (SocketException) { return false; }
    }

    private async Task StopAsync()
    {
        if (lifetime is not null) await lifetime.CancelAsync();
        if (observation is not null)
        {
            try { await observation; }
            catch (OperationCanceledException) { }
            catch (IOException) { /* Already recorded in the service log. */ }
        }
        process?.Dispose(); process = null;
        lifetime?.Dispose(); lifetime = null;
        observation = null;
        serviceExecutable = null;
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await StopAsync(); }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync() { await StopAsync(); gate.Dispose(); }
}

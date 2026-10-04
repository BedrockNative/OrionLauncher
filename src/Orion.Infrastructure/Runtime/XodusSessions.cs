using Orion.Domain;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Runtime;

public sealed record XodusSession(XodusEnvironment Environment, XodusService Service);

/// <summary>Launcher-owned services, reused across launches without sharing identities.</summary>
public sealed class XodusSessions(AppPaths paths, ProcessRunner runner) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, XodusSession> sessions = new(StringComparer.Ordinal);
    private readonly List<Task> starts = [];
    private CancellationTokenSource lifetime = new();
    private bool disposed;
    private Task? disposal;

    public async Task<XodusSession> GetAsync(RuntimeInstallation runtime, string? accountId, CancellationToken ct)
    {
        if (accountId is not null && (accountId.Length != 64 || !accountId.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("Invalid account ID.");
        XodusSession session;
        Task ready;
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lifetime.Token.ThrowIfCancellationRequested();
            var key = accountId ?? "";
            if (!sessions.TryGetValue(key, out session!))
            {
                // Stable, short socket names do not reveal the account identifier.
                var environment = XodusEnvironment.ForAccount(paths, accountId);
                session = new(environment, new(paths, environment, runner));
                sessions.Add(key, session);
            }
            starts.RemoveAll(task => { if (task.IsFaulted) _ = task.Exception; return task.IsCompleted; });
            ready = session.Service.EnsureAsync(runtime, lifetime.Token);
            starts.Add(ready);
        }
        finally { gate.Release(); }
        // A slow account/keyring must not block an already-ready account. Cancelling
        // one game also must not stop a service owned by the launcher.
        await ready.WaitAsync(ct);
        return session;
    }

    public async Task WarmAsync(RuntimeInstallation runtime, IEnumerable<string?> accountIds, CancellationToken ct)
    {
        List<Exception> failures = [];
        foreach (var account in accountIds.Distinct())
        {
            try { await GetAsync(runtime, account, ct); }
            catch (IOException error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException("Some Xodus services could not start.", failures);
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // The pool gate only protects scheduling, not service readiness.
            // Cancel after acquiring it so a cancelled reset cannot poison the
            // current generation or race with another reset replacing its token.
            lifetime.Cancel();
            await DrainStartsAsync();
            foreach (var session in sessions.Values) await session.Service.DisposeAsync();
            sessions.Clear();
            lifetime.Dispose(); lifetime = new();
        }
        finally { gate.Release(); }
    }

    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            await DrainStartsAsync();
            foreach (var session in sessions.Values) await session.Service.DisposeAsync();
            sessions.Clear();
            lifetime.Dispose();
        }
        finally { gate.Release(); }
    }

    private async Task DrainStartsAsync()
    {
        try { await Task.WhenAll(starts); }
        catch (Exception) { /* Startup callers report failures; shutdown still owns cleanup. */ }
        starts.Clear();
    }
}

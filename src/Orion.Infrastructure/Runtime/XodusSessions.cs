using Orion.Domain;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Runtime;

public sealed record XodusSession(XodusEnvironment Environment, XodusService Service);

/// <summary>Launcher-owned services, reused across launches without sharing identities.</summary>
public sealed class XodusSessions(AppPaths paths, ProcessRunner runner) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private sealed class Entry(XodusSession session)
    {
        public XodusSession Session { get; } = session;
        public CancellationTokenSource Lifetime { get; } = new();
        public List<Task> Starts { get; } = [];
    }

    private readonly Dictionary<string, Entry> sessions = new(StringComparer.Ordinal);
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
            var key = accountId ?? "";
            if (!sessions.TryGetValue(key, out var entry))
            {
                // Stable, short socket names do not reveal the account identifier.
                var environment = XodusEnvironment.ForAccount(paths, accountId);
                entry = new(new(environment, new(paths, environment, runner)));
                sessions.Add(key, entry);
            }
            session = entry.Session;
            entry.Starts.RemoveAll(task => { if (task.IsFaulted) _ = task.Exception; return task.IsCompleted; });
            ready = session.Service.EnsureAsync(runtime, entry.Lifetime.Token);
            entry.Starts.Add(ready);
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
            foreach (var entry in sessions.Values) entry.Lifetime.Cancel();
            foreach (var entry in sessions.Values) await StopEntryAsync(entry);
            sessions.Clear();
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
            foreach (var entry in sessions.Values) entry.Lifetime.Cancel();
            foreach (var entry in sessions.Values) await StopEntryAsync(entry);
            sessions.Clear();
        }
        finally { gate.Release(); }
    }

    // Reset only the removed account, including a pending keyring/startup wait.
    // Other accounts retain their service process and socket.
    public async Task ResetAccountAsync(string? accountId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!sessions.Remove(accountId ?? "", out var entry)) return;
            entry.Lifetime.Cancel();
            await StopEntryAsync(entry);
        }
        finally { gate.Release(); }
    }

    private static async Task StopEntryAsync(Entry entry)
    {
        try { await Task.WhenAll(entry.Starts); }
        catch (Exception) { /* Startup callers report failures; shutdown still owns cleanup. */ }
        await entry.Session.Service.DisposeAsync();
        entry.Lifetime.Dispose();
    }
}

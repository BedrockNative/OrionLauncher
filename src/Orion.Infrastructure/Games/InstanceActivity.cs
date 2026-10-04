using System.Collections.Concurrent;

namespace Orion.Infrastructure.Games;

/// <summary>A non-waiting lease shared by game sessions and content transactions.</summary>
public sealed class InstanceActivity : Orion.Application.IInstanceActivity
{
    private readonly ConcurrentDictionary<Guid, byte> active = new();
    public IDisposable Acquire(Guid id)
    {
        if (!active.TryAdd(id, 0))
            throw new InvalidOperationException("Stop the game and wait for the current content operation to finish first.");
        return new Lease(() => active.TryRemove(id, out _));
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
}

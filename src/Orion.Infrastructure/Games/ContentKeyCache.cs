using System.Security.Cryptography;

namespace Orion.Infrastructure.Games;

/// <summary>Account-scoped licensed keys. No durable cache; failures remain retryable.</summary>
public sealed class ContentKeyCache : IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<(string Account, Guid Content), byte[][]> keys = new();
    private bool disposed;

    public async Task<byte[][]> GetAsync(string account, Guid content,
        Func<CancellationToken, Task<byte[][]>> acquire, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!keys.TryGetValue((account, content), out var value))
            {
                value = await acquire(ct);
                if (value.Length == 0 || value.Any(k => k.Length != 48))
                {
                    foreach (var key in value) CryptographicOperations.ZeroMemory(key);
                    throw new InvalidDataException("Invalid content keys returned by Xodus.");
                }
                keys.Add((account, content), value);
            }
            return value.Select(k => k.ToArray()).ToArray();
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        gate.Wait();
        try
        {
            disposed = true;
            foreach (var value in keys.Values)
                foreach (var key in value) CryptographicOperations.ZeroMemory(key);
            keys.Clear();
        }
        finally { gate.Release(); }
    }
}

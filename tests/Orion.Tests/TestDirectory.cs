using Orion.Infrastructure.Storage;

namespace Orion.Tests;

public sealed class TestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"orion-test-{Guid.NewGuid():N}");
    public AppPaths Paths { get; }
    public TestDirectory()
    {
        Directory.CreateDirectory(Root);
        Paths = new(Path.Combine(Root, "data"), Path.Combine(Root, "config"), Path.Combine(Root, "cache"),
            Path.Combine(Root, "run"), Path.Combine(Root, "applications"));
        Paths.EnsureDirectories(); Directory.CreateDirectory(Paths.Runtime);
    }
    public void Dispose() => Directory.Delete(Root, recursive: true);
}

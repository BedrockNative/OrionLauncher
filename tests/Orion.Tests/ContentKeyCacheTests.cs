using Orion.Infrastructure.Games;

namespace Orion.Tests;

public sealed class ContentKeyCacheTests
{
    [Fact] public async Task CoalescesRequestsAndSeparatesAccountsAndPackages()
    {
        using var cache = new ContentKeyCache(); var calls = 0; var content = Guid.NewGuid();
        async Task<byte[][]> Acquire(CancellationToken ct) { Interlocked.Increment(ref calls); await Task.Delay(10, ct); return [new byte[48]]; }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetAsync("one", content, Acquire, default)));
        Assert.Equal(1, calls);
        results[0][0][0] = 42;
        Assert.Equal(0, results[1][0][0]);
        await cache.GetAsync("two", content, Acquire, default);
        await cache.GetAsync("one", Guid.NewGuid(), Acquire, default);
        Assert.Equal(3, calls);
    }
    [Fact] public async Task FailureIsNotCachedAndDisposalClearsOwnedBuffers()
    {
        var cache = new ContentKeyCache(); var content = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => cache.GetAsync("a", content, _ => throw new IOException(), default));
        byte[] owned = Enumerable.Repeat((byte)42, 48).ToArray();
        await cache.GetAsync("a", content, _ => Task.FromResult<byte[][]>([owned]), default);
        cache.Dispose(); Assert.All(owned, b => Assert.Equal(0, b));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.GetAsync("a", content, _ => throw new Exception(), default));
    }
    [Theory] [InlineData("..")] [InlineData("../x")] [InlineData("a\\b")] [InlineData("C:x")] [InlineData("/etc")] [InlineData("")]
    public void ExtractionRejectsUnsafeNames(string name) => Assert.Throws<InvalidDataException>(() => XvdPackageExtractor.ValidateName(name));

    [Fact] public void FullExtractionAcceptsValidPeAndRejectsTruncation()
    {
        using var d = new TestDirectory();
        var exe = Path.Combine(d.Root, "Minecraft.Windows.exe");
        File.WriteAllText(Path.Combine(d.Root, XvdPackageExtractor.Marker), "1");
        var bytes = OrionOptimizationTests.Image();
        File.WriteAllBytes(exe, bytes);
        Assert.Equal(exe, GameLayout.ValidatedExecutable(d.Root));
        File.WriteAllBytes(exe, bytes[..^1]);
        Assert.Throws<InvalidDataException>(() => GameLayout.Validate(d.Root));
    }

    [Fact] public void DirectLaunchPreservesArgumentBoundariesAndDoesNotInvokeXodus()
    {
        var command = InstanceLaunchPlan.CreateDirect("/runtime/wine", "/game folder/Minecraft.Windows.exe", "/game folder",
            new Orion.Domain.InstanceLaunchOptions { Arguments = ["a b"], LaunchCommand = "gamemoderun %command%" }, new Dictionary<string, string?>());
        Assert.Equal("gamemoderun", command.Executable);
        Assert.Equal(new[] { "/runtime/wine", "/game folder/Minecraft.Windows.exe", "a b" }, command.Arguments);
    }
}

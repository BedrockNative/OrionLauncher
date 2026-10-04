using System.Security.Cryptography;
using Orion.Infrastructure.CurseForge;

namespace Orion.Tests;

public sealed class BrowserDownloadTests
{
    private sealed class InlineProgress(Action<BrowserDownloadState> report) : IProgress<BrowserDownloadState>
    { public void Report(BrowserDownloadState value) => report(value); }
    internal static CfFile Metadata(byte[] data, string name = "example.mcpack") =>
        new(99, 42, 78022, "Example", name, data.Length, true, null,
            [new(Convert.ToHexString(SHA1.HashData(data)), 1)], ["1.21"], []);
    private static BrowserDownloadMonitor Monitor() => new(TimeSpan.FromMilliseconds(15));
    private static TaskCompletionSource Observed() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    [Theory]
    [InlineData("example.mcpack")]
    [InlineData("example (1).mcpack")]
    [InlineData("EXAMPLE(2).MCPACK")]
    public async Task FindsExistingOrBrowserRenamedDownloadAndPreservesOriginal(string name)
    {
        using var dir = new TestDirectory(); var downloads = Directory.CreateDirectory(Path.Combine(dir.Root, "Downloads")).FullName;
        var bytes = new byte[] { 1, 2, 3 }; var original = Path.Combine(downloads, name);
        await File.WriteAllBytesAsync(original, bytes); var target = Path.Combine(dir.Root, "snapshot.mcpack");
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Monitor().WaitAsync(Metadata(bytes), () => downloads, target, null, ct.Token);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target)); Assert.Equal(bytes, await File.ReadAllBytesAsync(original));
    }
    [Fact]
    public async Task IgnoresPartialFilesAndWaitsForBrowserRename()
    {
        using var dir = new TestDirectory(); var downloads = Directory.CreateDirectory(Path.Combine(dir.Root, "Downloads")).FullName;
        var data = new byte[] { 1, 2, 3 }; var partial = Path.Combine(downloads, "example.mcpack.crdownload");
        await File.WriteAllBytesAsync(partial, data);
        var observed = Observed(); var target = Path.Combine(dir.Root, "snapshot");
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = Monitor().WaitAsync(Metadata(data), () => downloads, target,
            new InlineProgress(s => { if (s == BrowserDownloadState.Waiting) observed.TrySetResult(); }), ct.Token);
        await observed.Task.WaitAsync(ct.Token); Assert.False(task.IsCompleted); Assert.False(File.Exists(target));
        File.Move(partial, Path.Combine(downloads, "example.mcpack"));
        await task; Assert.Equal(data, await File.ReadAllBytesAsync(target));
    }
    [Fact]
    public async Task RejectsWrongHashAndAcceptsCorrectDuplicateWithoutTouchingEitherDownload()
    {
        using var dir = new TestDirectory(); var downloads = Directory.CreateDirectory(Path.Combine(dir.Root, "Downloads")).FullName;
        var original = Path.Combine(downloads, "example.mcpack"); var data = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(original, [4, 5, 6]); var target = Path.Combine(dir.Root, "snapshot");
        var mismatch = Observed(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = Monitor().WaitAsync(Metadata(data), () => downloads, target,
            new InlineProgress(s => { if (s == BrowserDownloadState.Mismatch) mismatch.TrySetResult(); }), ct.Token);
        await mismatch.Task.WaitAsync(ct.Token); Assert.False(File.Exists(target));
        var correct = Path.Combine(downloads, "example (1).mcpack"); await File.WriteAllBytesAsync(correct, data);
        await task; Assert.Equal(data, await File.ReadAllBytesAsync(target));
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(original)); Assert.True(File.Exists(correct));
    }
    [Fact]
    public async Task CanChangeFromMissingFolderWithoutRestartingOperation()
    {
        using var dir = new TestDirectory(); var folder = Path.Combine(dir.Root, "missing");
        var missing = Observed(); var target = Path.Combine(dir.Root, "snapshot");
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = Monitor().WaitAsync(Metadata([1, 2]), () => Volatile.Read(ref folder), target,
            new InlineProgress(s => { if (s == BrowserDownloadState.MissingFolder) missing.TrySetResult(); }), ct.Token);
        await missing.Task.WaitAsync(ct.Token);
        var chosen = Directory.CreateDirectory(Path.Combine(dir.Root, "chosen")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(chosen, "example.mcpack"), [1, 2]); Volatile.Write(ref folder, chosen);
        await task; Assert.True(File.Exists(target)); Assert.False(Directory.Exists(Path.Combine(dir.Root, "missing")));
    }
    [Fact]
    public async Task DoesNotScanSubfoldersOrFollowFileSymlinksAndCancellationStopsWaiting()
    {
        using var dir = new TestDirectory(); var downloads = Directory.CreateDirectory(Path.Combine(dir.Root, "Downloads")).FullName;
        var sub = Directory.CreateDirectory(Path.Combine(downloads, "sub")).FullName;
        var original = Path.Combine(sub, "example.mcpack"); await File.WriteAllBytesAsync(original, [1, 2]);
        File.CreateSymbolicLink(Path.Combine(downloads, "example.mcpack"), original);
        var target = Path.Combine(dir.Root, "snapshot"); var observed = Observed();
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = Monitor().WaitAsync(Metadata([1, 2]), () => downloads, target,
            new InlineProgress(_ => observed.TrySetResult()), ct.Token);
        await observed.Task.WaitAsync(ct.Token); await Task.Delay(70); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(File.Exists(target)); Assert.True(File.Exists(original));
    }
    [Fact]
    public async Task CancellationDuringVerificationRemovesOnlyStagingCopy()
    {
        using var dir = new TestDirectory(); var downloads = Directory.CreateDirectory(Path.Combine(dir.Root, "Downloads")).FullName;
        var original = Path.Combine(downloads, "example.mcpack"); await File.WriteAllBytesAsync(original, [1, 2]);
        var target = Path.Combine(dir.Root, "snapshot"); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = Monitor().WaitAsync(Metadata([1, 2]), () => downloads, target,
            new InlineProgress(s => { if (s == BrowserDownloadState.Verifying) ct.Cancel(); }), ct.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(File.Exists(target)); Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(original));
    }
    [Fact]
    public async Task MissingHashAndExistingStagingAreRejected()
    {
        using var dir = new TestDirectory(); var target = Path.Combine(dir.Root, "snapshot");
        await Assert.ThrowsAsync<InvalidDataException>(() => Monitor().WaitAsync(Metadata([1]) with { Hashes = [] }, () => dir.Root, target, null, default));
        await File.WriteAllTextAsync(target, "preserve");
        await Assert.ThrowsAsync<IOException>(() => Monitor().WaitAsync(Metadata([1]), () => dir.Root, target, null, default));
        Assert.Equal("preserve", await File.ReadAllTextAsync(target));
    }
    [Fact]
    public async Task FinalNameIsNotReadWhileBrowserSidecarExists()
    {
        using var dir = new TestDirectory(); var downloads = Directory.CreateDirectory(Path.Combine(dir.Root, "Downloads")).FullName;
        var original = Path.Combine(downloads, "example.mcpack"); await File.WriteAllBytesAsync(original, [1, 2]);
        var partial = original + ".part"; await File.WriteAllTextAsync(partial, "incomplete");
        var target = Path.Combine(dir.Root, "snapshot"); var observed = Observed(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = Monitor().WaitAsync(Metadata([1, 2]), () => downloads, target, new InlineProgress(_ => observed.TrySetResult()), ct.Token);
        await observed.Task.WaitAsync(ct.Token); await Task.Delay(70); Assert.False(File.Exists(target));
        File.Delete(partial); await task; Assert.True(File.Exists(original));
    }
}

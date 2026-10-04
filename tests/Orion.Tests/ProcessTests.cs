using Orion.Infrastructure.Processes;

namespace Orion.Tests;

public sealed class ProcessTests
{
    [Fact]
    public async Task ConcurrentPartialStreamsDoNotSpliceNormalLines()
    {
        using var dir = new TestDirectory(); var log = Path.Combine(dir.Root, "process.log");
        await new ProcessRunner().RunAsync(new("/bin/sh", ["-c", "printf 'out-start'; printf 'err-start' >&2; sleep .05; printf -- '-end\\n'; printf -- '-end\\n' >&2"], dir.Root), log, default);
        var text = File.ReadAllText(log);
        Assert.Contains("out-start-end\n", text); Assert.Contains("err-start-end\n", text);
    }

    [Fact]
    public async Task DrainsBothStreamsAndPreservesLiteralArguments()
    {
        using var directory = new TestDirectory();
        var log = Path.Combine(directory.Root, "process.log");
        await new ProcessRunner().RunAsync(new("/bin/sh", ["-c", "printf '%s' \"$1\"; printf 'stderr' >&2", "test", "$(touch nope) $HOME"], directory.Root), log, CancellationToken.None);
        Assert.Contains("$(touch nope) $HOME", await File.ReadAllTextAsync(log));
        Assert.Contains("stderr", await File.ReadAllTextAsync(log));
        Assert.False(File.Exists(Path.Combine(directory.Root, "nope")));
    }

    [Fact]
    public async Task CancellingTerminatesTheProcessTree()
    {
        using var directory = new TestDirectory();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var task = new ProcessRunner().RunAsync(new("/bin/sh", ["-c", "sleep 60 & wait"], directory.Root),
            Path.Combine(directory.Root, "process.log"), cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}

using System.Diagnostics;
using System.Globalization;
using Orion.Infrastructure.Processes;
using Orion.Desktop;

namespace Orion.Tests;

public sealed class ProcessSupervisorTests
{
    [Fact]
    public async Task HeadlessSupervisorPreservesArgumentsAndExitStatus()
    {
        using var directory = new TestDirectory();
        var runner = new ProcessRunner(["dotnet", typeof(App).Assembly.Location]);
        var result = await runner.CaptureAsync(new("/usr/bin/printf", ["%s", "literal $HOME ; value"], directory.Root), default);
        Assert.Equal("literal $HOME ; value", result);
        var log = Path.Combine(directory.Root, "supervised.log");
        await runner.RunAsync(new("/usr/bin/printf", ["ok\\n"], directory.Root), log, default);
        Assert.Contains("[printf] Exited with code 0", File.ReadAllText(log));
        Assert.DoesNotContain("[dotnet] Exited", File.ReadAllText(log));
    }

    [Fact]
    public async Task KillingParentStopsOwnedWorkerWithoutTouchingUnrelatedProcesses()
    {
        using var directory = new TestDirectory();
        var marker = Path.Combine(directory.Root, "child.pid");
        using var parent = Process.Start(new ProcessStartInfo("/bin/sleep", "60") { UseShellExecute = false })!;
        using var unrelated = Process.Start(new ProcessStartInfo("/bin/sleep", "60") { UseShellExecute = false })!;
        var task = ProcessSupervisor.RunAsync([parent.Id.ToString(CultureInfo.InvariantCulture), ProcessSupervisor.StartIdentity(parent.Id),
            "/bin/sh", "-c", "echo $$ > \"$1\"; exec sleep 60", "fixture", marker]);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(marker)) await Task.Delay(10, timeout.Token);
            parent.Kill(); await parent.WaitForExitAsync(timeout.Token);
            Assert.NotEqual(0, await task.WaitAsync(timeout.Token));
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            if (!parent.HasExited) parent.Kill();
            if (!unrelated.HasExited) unrelated.Kill();
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}

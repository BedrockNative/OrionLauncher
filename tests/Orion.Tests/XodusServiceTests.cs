using System.Net.Sockets;
using Orion.Domain;
using Orion.Infrastructure.Runtime;

namespace Orion.Tests;

public sealed class XodusServiceTests
{
    [Fact]
    public async Task WaitsBeyondTheOldTimeoutForTheDesktopUnlockPrompt()
    {
        using var directory = new TestDirectory();
        var runtime = Path.Combine(directory.Root, "xodus");
        Directory.CreateDirectory(runtime);
        var executable = Path.Combine(runtime, "xodus-service");
        // A synthetic service waits like the native password prompt. This test
        // never connects to, locks, or changes the user's actual desktop keyring.
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nexec sleep 60\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var service = new XodusService(directory.Paths, new(directory.Paths));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var startup = service.EnsureAsync(new RuntimeInstallation("xodus", "test", runtime), cancellation.Token);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(21), cancellation.Token);
            Assert.False(startup.IsCompleted, "The service was stopped while the user could still be unlocking the keyring.");
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(directory.Paths.SocketPath));
            socket.Listen(1);
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await startup; }
            catch (OperationCanceledException) { }
        }
    }
}

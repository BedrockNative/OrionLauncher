using Orion.Infrastructure.Processes;

namespace Orion.Infrastructure.Runtime;

public static class WineServer
{
    public static async Task StopAsync(ProcessRunner runner, string executable, string workingDirectory,
        IReadOnlyDictionary<string, string?> environment, string log)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await runner.RunAsync(new(executable, ["-k"], workingDirectory, environment), log, timeout.Token); }
        catch (ProcessFailureException ex) when (ex.ExitCode == 1)
        {
            // wineserver -k returns 1 when no process holds this prefix's lock.
            // -w below still validates access to the prefix and waits for any remaining owner.
        }
        await runner.RunAsync(new(executable, ["-w"], workingDirectory, environment), log, timeout.Token);
    }
}

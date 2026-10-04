using System.Diagnostics;
using System.Globalization;

namespace Orion.Infrastructure.Processes;

/// <summary>Headless Orion child mode. Tracks the parent process, not a transient thread or socket name.</summary>
public static class ProcessSupervisor
{
    public const string Switch = "--supervise-child";

    public static ProcessCommand Wrap(ProcessCommand command, IReadOnlyList<string> host)
    {
        using var parent = Process.GetCurrentProcess();
        return new(host[0], [.. host.Skip(1), Switch, parent.Id.ToString(CultureInfo.InvariantCulture),
            StartIdentity(parent.Id), command.Executable, .. command.Arguments],
            command.WorkingDirectory, command.Environment);
    }

    public static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length < 3 || !int.TryParse(arguments[0], out var pid)) return 2;
        Process parent;
        try { parent = Process.GetProcessById(pid); }
        catch (ArgumentException) { return 2; }
        using (parent)
        {
            if (parent.HasExited || StartIdentity(pid) != arguments[1]) return 2;
            var info = new ProcessStartInfo(arguments[2]) { UseShellExecute = false };
            info.Environment.Remove(CurseForge.CurseForgeCredential.EnvironmentKey);
            info.Environment.Remove(CurseForge.CurseForgeCredential.EnvironmentFile);
            foreach (var argument in arguments.Skip(3)) info.ArgumentList.Add(argument);
            using var child = Process.Start(info) ?? throw new IOException("Could not start supervised process.");
            using var observer = new CancellationTokenSource();
            var childExit = child.WaitForExitAsync();
            var parentExit = parent.WaitForExitAsync(observer.Token);
            try
            {
                if (await Task.WhenAny(childExit, parentExit) == parentExit)
                {
                    try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
                await childExit;
                return child.ExitCode;
            }
            finally
            {
                await observer.CancelAsync();
                try { await parentExit; } catch (OperationCanceledException) { }
            }
        }
    }

    // Kernel clock ticks are stable across readers; Process.StartTime is reconstructed
    // from wall-clock time on Linux and can differ slightly between processes.
    public static string StartIdentity(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        return stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19];
    }
}

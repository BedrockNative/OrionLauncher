using System.Diagnostics;

namespace Orion.Infrastructure.Processes;

public sealed record ProcessCommand(string Executable, IReadOnlyList<string> Arguments,
    string WorkingDirectory, IReadOnlyDictionary<string, string?>? Environment = null);

public sealed class ProcessRunner(IReadOnlyList<string>? supervisorHost = null)
{
    public ProcessStartInfo OwnedStartInfo(ProcessCommand command) => StartInfo(
        supervisorHost is { Count: > 0 } ? ProcessSupervisor.Wrap(command, supervisorHost) : command);
    /// <summary>Structured command output stays in memory, not in diagnostic logs.</summary>
    public async Task<string> CaptureAsync(ProcessCommand command, CancellationToken ct,
        string failureMessage = "Xodus could not read or update accounts. Unlock the keyring and refresh accounts.")
    {
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(OwnedStartInfo(command)) ?? throw new IOException("Could not start the command.");
        using var registration = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        async Task<string> ReadBoundedAsync(StreamReader reader)
        {
            var output = new System.Text.StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
            {
                if (output.Length + count > 1024 * 1024)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    throw new InvalidDataException("Account command output exceeded its limit.");
                }
                output.Append(buffer, 0, count);
            }
            return output.ToString();
        }
        var output = ReadBoundedAsync(process.StandardOutput);
        var error = ReadBoundedAsync(process.StandardError);
        await Task.WhenAll(output, error, process.WaitForExitAsync(CancellationToken.None));
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException(failureMessage);
        return await output;
    }

    public static ProcessStartInfo StartInfo(ProcessCommand command)
    {
        var info = new ProcessStartInfo(command.Executable)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
        if (command.Environment is not null)
            foreach (var (key, value) in command.Environment)
                if (value is null) info.Environment.Remove(key); else info.Environment[key] = value;
        info.Environment.Remove(CurseForge.CurseForgeCredential.EnvironmentKey);
        info.Environment.Remove(CurseForge.CurseForgeCredential.EnvironmentFile);
        return info;
    }

    public async Task RunAsync(ProcessCommand command, string logFile, CancellationToken ct, Action<string>? outputReceived = null)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        ProcessLog.Event(logFile, Path.GetFileName(command.Executable), "Starting");
        using var process = Process.Start(OwnedStartInfo(command)) ?? throw new IOException($"Could not start {command.Executable}.");
        try { await ObserveAsync(process, logFile, ct, outputReceived, Path.GetFileName(command.Executable)); }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            throw;
        }
    }

    public static async Task ObserveAsync(Process process, string logFile, CancellationToken ct, Action<string>? outputReceived = null, string? source = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        // Drain both streams concurrently to prevent child-process pipe deadlocks.
        async Task Drain(StreamReader reader)
        {
            try
            {
                var buffer = new char[4096];
                var line = new System.Text.StringBuilder();
                void Flush()
                {
                    var text = line.ToString(); line.Clear();
                    ProcessLog.Append(logFile, text);
                    outputReceived?.Invoke(text);
                }
                int length;
                while ((length = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
                {
                    // Keep stdout/stderr from splicing each other's lines. Bound
                    // unterminated output so a child cannot grow memory indefinitely.
                    for (var i = 0; i < length; i++)
                    {
                        line.Append(buffer[i]);
                        if (buffer[i] == '\n' || line.Length >= 8192) Flush();
                    }
                }
                if (line.Length > 0) Flush();
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
        }
        var output = Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError));
        using var registration = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        await process.WaitForExitAsync(CancellationToken.None);
        await output;
        ProcessLog.Event(logFile, source ?? Path.GetFileName(process.StartInfo.FileName), $"Exited with code {process.ExitCode}");
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new ProcessFailureException(process.StartInfo.FileName, process.ExitCode, logFile);
    }
}

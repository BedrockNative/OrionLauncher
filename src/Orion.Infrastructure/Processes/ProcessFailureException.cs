namespace Orion.Infrastructure.Processes;

public sealed class ProcessFailureException(string executable, int exitCode, string logFile)
    : IOException($"{Path.GetFileName(executable)} exited with code {exitCode}. See {logFile}")
{
    public int ExitCode { get; } = exitCode;
}

using System.Diagnostics;
using System.Globalization;
using Orion.Domain;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Runtime;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Games;

/// <summary>Routes installation, prefix, game and shared-service diagnostics to one instance journal.</summary>
public sealed class InstanceJournal : IDisposable, IProgress<OperationProgress>
{
    private IXodusService? service;
    private readonly IProgress<OperationProgress>? progress;
    public string Path { get; }

    public InstanceJournal(AppPaths paths, Guid id, IXodusService? service, IProgress<OperationProgress>? progress)
    {
        this.service = service; this.progress = progress; Path = paths.InstanceLog(id);
        // An update installer runs inside the launch journal; let the outer journal write each event once.
        nested = progress is InstanceJournal outer && outer.Path == Path;
        if (service is not null) service.OutputReceived += ServiceOutput;
    }
    public void AttachService(IXodusService value)
    {
        if (service is not null) service.OutputReceived -= ServiceOutput;
        service = value; service.OutputReceived += ServiceOutput;
    }

    private void ServiceOutput(string text) => ProcessLog.Event(Path, "Xodus service", text);
    private readonly bool nested;
    private string? phase;
    private long phaseStarted;
    public void Report(OperationProgress value)
    {
        if (!nested) Write(value);
        progress?.Report(value);
    }
    private void Write(OperationProgress value)
    {
        // Measured phases ("Downloading package · 1.0 / 2.0 MiB") update one line, ending with elapsed time.
        var separator = value.Message.IndexOf(" · ", StringComparison.Ordinal);
        if (value.Fraction is null || separator < 0)
        {
            phase = null;
            ProcessLog.Event(Path, "Orion", value.Message);
            return;
        }
        var key = value.Message[..separator];
        if (key != phase) { phase = key; phaseStarted = Stopwatch.GetTimestamp(); }
        ProcessLog.Progress(Path, "Orion", key, $"{value.Message} · {FormatElapsed(Stopwatch.GetElapsedTime(phaseStarted))}");
    }
    public static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    public void Error(Exception error)
    {
        if (!nested) ProcessLog.Event(Path, "Orion", error is OperationCanceledException ? "Cancelled" : error.Message);
    }
    public void Dispose() { if (service is not null) service.OutputReceived -= ServiceOutput; }
}

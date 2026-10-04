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
        if (service is not null) service.OutputReceived += ServiceOutput;
    }
    public void AttachService(IXodusService value)
    {
        if (service is not null) service.OutputReceived -= ServiceOutput;
        service = value; service.OutputReceived += ServiceOutput;
    }

    private void ServiceOutput(string text) => ProcessLog.Event(Path, "Xodus service", text);
    public void Report(OperationProgress value)
    {
        ProcessLog.Event(Path, "Orion", value.Message);
        progress?.Report(value);
    }
    public void Error(Exception error) => ProcessLog.Event(Path, "Orion", error is OperationCanceledException ? "Cancelled" : error.Message);
    public void Dispose() { if (service is not null) service.OutputReceived -= ServiceOutput; }
}

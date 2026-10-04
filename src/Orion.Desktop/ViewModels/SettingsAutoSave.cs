using Orion.Infrastructure.Storage;

namespace Orion.Desktop.ViewModels;

/// <summary>UI-thread-owned, last-value-wins queue. Writes never overlap; shutdown awaits the queue.</summary>
public sealed class SettingsAutoSave(Func<LauncherSettings, Task> persist, Action<Exception> failed)
{
    private LauncherSettings? next;
    private LauncherSettings? lastRequested;
    private Task worker = Task.CompletedTask;
    public void Queue(LauncherSettings settings)
    {
        if (settings == lastRequested) return;
        lastRequested = next = settings;
        if (worker.IsCompleted) worker = DrainAsync();
    }
    private async Task DrainAsync()
    {
        while (next is not null)
        {
            await Task.Delay(250);
            var snapshot = next; next = null;
            try { await persist(snapshot); }
            catch (Exception ex) { if (lastRequested == snapshot) lastRequested = null; failed(ex); }
        }
    }
    public Task FlushAsync() => worker;
}

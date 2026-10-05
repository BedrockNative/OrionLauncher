using CommunityToolkit.Mvvm.ComponentModel;
using Orion.Desktop.I18n;
using Orion.Infrastructure.Releases;

namespace Orion.Desktop.ViewModels;

/// <summary>Non-blocking startup check; never downloads or installs runtimes.</summary>
public partial class RuntimeUpdatesViewModel(Localizer text,
    Func<RuntimeDefinition, CancellationToken, Task<RuntimeUpdate>> check) : ObservableObject
{
    private CancellationTokenSource? cancellation;
    private Task pending = Task.CompletedTask;
    private bool started;
    private bool completed;
    private readonly List<RuntimeUpdate> updates = [];
    private readonly List<string> failures = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Status))] private bool isChecking;
    public bool HasUpdates => updates.Count != 0;
    public string Status
    {
        get
        {
            if (IsChecking) return text["RuntimeChecking"];
            var available = string.Join(" · ", updates.Select(u => $"{u.Name}: {u.LatestTag}"));
            var result = HasUpdates ? text["RuntimeUpdatesAvailable"] + " " + available
                : completed && failures.Count == 0 ? text["RuntimeUpToDate"] : "";
            if (failures.Count != 0) result += (result.Length == 0 ? "" : "\n")
                + text["RuntimeCheckFailed"] + " " + string.Join(", ", failures);
            return result;
        }
    }

    public Task CheckOnStartupAsync(bool enabled)
    {
        if (started) return pending;
        started = true;
        if (!enabled) return pending;
        cancellation = new();
        return pending = CheckAsync(cancellation);
    }

    private async Task CheckAsync(CancellationTokenSource source)
    {
        IsChecking = true;
        try
        {
            foreach (var runtime in new[] { RuntimeDefinition.Xodus, RuntimeDefinition.WineGdk })
            {
                try
                {
                    var result = await check(runtime, source.Token);
                    source.Token.ThrowIfCancellationRequested();
                    if (result.Available) updates.Add(result);
                }
                catch (OperationCanceledException) when (source.IsCancellationRequested) { return; }
                catch (Exception) { failures.Add(runtime.Name); }
            }
            completed = true;
        }
        finally
        {
            if (source.IsCancellationRequested) { updates.Clear(); failures.Clear(); }
            cancellation = null;
            source.Dispose();
            IsChecking = false;
            RefreshLabels();
        }
    }

    public void Cancel() => cancellation?.Cancel();
    public async Task StopAsync() { started = true; Cancel(); await pending; }
    public void Clear()
    {
        updates.Clear(); failures.Clear(); completed = false;
        RefreshLabels();
    }
    public void RefreshLabels()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasUpdates));
    }
}

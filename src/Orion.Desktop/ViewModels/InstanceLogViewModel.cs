using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orion.Desktop.I18n;
using Orion.Infrastructure.Processes;

namespace Orion.Desktop.ViewModels;

public partial class InstanceLogViewModel : ObservableObject
{
    private readonly DispatcherTimer timer;
    private bool reading;
    private int revision;
    private string? path;
    public string? LogPath => path;
    public bool IsDetached { get; }
    public Localizer Text { get; }
    [ObservableProperty] private bool isOpen;
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string content = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string? error;
    public bool HasError => Error is not null;
    [ObservableProperty] private bool follow = true;
    [ObservableProperty] private string copyStatus = "";
    public InstanceLogViewModel(Localizer text, bool isDetached = false)
    {
        Text = text;
        IsDetached = isDetached;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += async (_, _) => await RefreshAsync();
    }
    public async Task OpenAsync(string instanceName, string logPath)
    {
        revision++;
        Name = instanceName; path = logPath; Content = ""; Error = null; CopyStatus = ""; IsOpen = true;
        timer.Start(); await RefreshAsync();
    }
    public async Task CopyAsync(Func<string, Task> writeClipboard)
    {
        try { await writeClipboard(Content); CopyStatus = Text["LogCopied"]; }
        catch (Exception) { CopyStatus = Text["LogCopyError"]; }
    }
    public async Task RefreshAsync()
    {
        if (reading || path is null || !IsOpen) return;
        reading = true;
        var selected = path;
        var version = revision;
        try
        {
            var text = await LogTail.ReadAsync(selected);
            if (IsOpen && path == selected && version == revision) { Content = text; Error = null; }
        }
        catch (IOException) { Error = Text["LogReadError"]; }
        catch (UnauthorizedAccessException) { Error = Text["LogReadError"]; }
        finally { reading = false; }
    }
    public async Task ClearAsync(Func<Task<bool>> confirm)
    {
        var selected = path;
        if (selected is null || !await confirm() || selected != path) return;
        try
        {
            revision++;
            ProcessLog.Clear(selected);
            Content = ""; Error = null; CopyStatus = Text["LogsCleared"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { Error = Text["LogsClearFailed"] + " " + ex.Message; }
    }
    [RelayCommand] public void Close() { timer.Stop(); IsOpen = false; }
}

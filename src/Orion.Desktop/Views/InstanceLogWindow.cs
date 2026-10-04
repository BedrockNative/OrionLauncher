using Avalonia.Controls;
using Orion.Desktop.ViewModels;

namespace Orion.Desktop.Views;

public sealed class InstanceLogWindow : Window
{
    public InstanceLogViewModel Log { get; }
    public InstanceLogWindow(InstanceLogViewModel log)
    {
        Log = log;
        Title = $"Orion · {log.Name}";
        Width = 960; Height = 520; MinWidth = 640; MinHeight = 260;
        Content = new InstanceLogPanel { DataContext = log, Margin = new Avalonia.Thickness(0, 16, 0, 0) };
        log.PropertyChanged += OnLogChanged;
        Closed += (_, _) => { log.PropertyChanged -= OnLogChanged; log.Close(); };
    }
    private void OnLogChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Log.IsOpen) && !Log.IsOpen) Close();
    }
}

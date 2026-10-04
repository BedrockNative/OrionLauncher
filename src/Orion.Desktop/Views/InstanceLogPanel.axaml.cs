using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Orion.Desktop.ViewModels;

namespace Orion.Desktop.Views;

public partial class InstanceLogPanel : UserControl
{
    private InstanceLogViewModel? model;
    public InstanceLogPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind();
        DetachedFromVisualTree += (_, _) => { if (model is not null) model.PropertyChanged -= Changed; model = null; };
        AttachedToVisualTree += (_, _) => Bind();
    }
    private void Bind()
    {
        if (model is not null) model.PropertyChanged -= Changed;
        model = DataContext as InstanceLogViewModel;
        if (model is not null) model.PropertyChanged += Changed;
    }
    private async void CopyLog(object? sender, RoutedEventArgs e)
    {
        if (model is not { } current) return;
        await current.CopyAsync(text => TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard
            ? clipboard.SetTextAsync(text) : Task.FromException(new InvalidOperationException("Clipboard unavailable.")));
    }
    private async void DetachLog(object? sender, RoutedEventArgs e)
    {
        if (model is { } current && TopLevel.GetTopLevel(this) is MainWindow window)
            await window.DetachLogAsync(current);
    }
    private async void ClearLog(object? sender, RoutedEventArgs e)
    {
        if (model is { } current && TopLevel.GetTopLevel(this) is Window owner)
            await current.ClearAsync(() => MainWindow.ConfirmOwnedAsync(owner, current.Text["ClearLog"],
                current.Text["ClearLogConfirm"], current.Text["ClearLog"], current.Text["Cancel"]));
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (model?.Follow == true && e.PropertyName is nameof(InstanceLogViewModel.Content) or nameof(InstanceLogViewModel.Follow))
            Dispatcher.UIThread.Post(() => Output.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd(), DispatcherPriority.Background);
    }
}

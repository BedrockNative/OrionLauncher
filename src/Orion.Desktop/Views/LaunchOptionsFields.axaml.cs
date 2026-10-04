using Avalonia.Controls;
using Avalonia.Interactivity;
using Orion.Desktop.ViewModels;

namespace Orion.Desktop.Views;

public partial class LaunchOptionsFields : UserControl
{
    private Screens? screens;
    public LaunchOptionsFields()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            screens = TopLevel.GetTopLevel(this)?.Screens;
            if (screens is not null) screens.Changed += RefreshScreens;
            RefreshScreens(this, EventArgs.Empty);
        };
        DetachedFromVisualTree += (_, _) => { if (screens is not null) screens.Changed -= RefreshScreens; screens = null; };
        DataContextChanged += (_, _) => RefreshScreens(this, EventArgs.Empty);
    }
    private void RefreshScreens(object? sender, EventArgs e)
    {
        if (screens is null || DataContext is not LaunchOptionsViewModel model) return;
        model.SetMonitorResolutions(screens.All.Select((screen, index) => new MonitorResolutionChoice(
            $"{screen.DisplayName ?? model.Text["Monitor"] + " " + (index + 1)} · {screen.Bounds.Width} × {screen.Bounds.Height}",
            new(screen.Bounds.Width, screen.Bounds.Height))));
    }
    private async void OpenContent(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LaunchOptionsViewModel { Card: { } card } || TopLevel.GetTopLevel(this) is not MainWindow window) return;
        await card.Owner.ManageContentAsync(card, (model, ct) => new ContentWindow(model).OpenAsync(window, ct));
    }
    private async void OpenInstanceLog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LaunchOptionsViewModel { Card: { } card } || TopLevel.GetTopLevel(this) is not MainWindow window) return;
        try
        {
            await card.Owner.ShowInstanceLogAsync(card);
            await window.DetachLogAsync(card.Owner.Log);
        }
        catch (Exception ex) { card.Owner.Error = ex.Message; }
    }
    private async void OpenCoverLink(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LaunchOptionsViewModel model) return;
        var uri = sender is Button { CommandParameter: "license" }
            ? new Uri("https://creativecommons.org/licenses/by/3.0/") : model.CoverSource;
        if (uri is null) return;
        try
        {
            if (TopLevel.GetTopLevel(this) is not { } top || !await top.Launcher.LaunchUriAsync(uri))
                model.CoverLinkError = model.Text["BrowserFailed"];
        }
        catch (Exception) { model.CoverLinkError = model.Text["BrowserFailed"]; }
    }
}

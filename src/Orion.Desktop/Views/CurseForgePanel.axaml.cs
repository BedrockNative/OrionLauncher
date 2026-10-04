using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Orion.Desktop.ViewModels;
using Orion.Infrastructure.CurseForge;

namespace Orion.Desktop.Views;

public partial class CurseForgePanel : UserControl
{
    public static readonly StyledProperty<double> ProjectCardWidthProperty = AvaloniaProperty.Register<CurseForgePanel, double>(nameof(ProjectCardWidth), 280);
    public double ProjectCardWidth { get => GetValue(ProjectCardWidthProperty); set => SetValue(ProjectCardWidthProperty, value); }
    public CurseForgePanel()
    {
        InitializeComponent();
        ProjectList.SizeChanged += (_, _) => UpdateCardWidth();
        DataContextChanged += (_, _) => BindLayout();
        SizeChanged += (_, _) => UpdateLayoutMode();
        DetachedFromVisualTree += (_, _) => UnbindLayout();
        AttachedToVisualTree += async (_, _) =>
        {
            BindLayout();
            if (DataContext is not CurseForgeViewModel model || TopLevel.GetTopLevel(this) is not { } top) return;
            model.OpenBrowserAsync ??= uri => top.Launcher.LaunchUriAsync(uri);
            try
            {
                using var folder = await top.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads);
                if (!model.IsBusy && folder?.TryGetLocalPath() is { } path) model.DownloadFolder = path;
            }
            catch (Exception) { /* The folder remains manually selectable. */ }
        };
    }
    private CurseForgeViewModel? bound;
    private void UnbindLayout() { if (bound is not null) bound.PropertyChanged -= ModelChanged; bound = null; }
    private void BindLayout()
    {
        UnbindLayout(); bound = DataContext as CurseForgeViewModel;
        if (bound is not null) bound.PropertyChanged += ModelChanged;
        UpdateLayoutMode();
    }
    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(CurseForgeViewModel.HasProject)) UpdateLayoutMode(); }
    private void UpdateLayoutMode()
    {
        // Full-width discovery until a project is opened; narrow windows show details on their own.
        var selected = bound?.HasProject == true;
        var narrow = Bounds.Width < 850;
        ProjectResults.IsVisible = !selected || !narrow;
        Grid.SetColumnSpan(ProjectResults, selected ? 1 : 2);
        if (ProjectDetails is not null)
        {
            Grid.SetColumn(ProjectDetails, narrow ? 0 : 1);
            Grid.SetColumnSpan(ProjectDetails, narrow ? 2 : 1);
        }
    }
    private void UpdateCardWidth()
    {
        // Share the viewport evenly instead of leaving a fixed-width strip unused.
        // Reserve the vertical scrollbar and the 6px item margins on each side.
        var available = ProjectList.Bounds.Width - 20;
        if (available <= 0) return;
        var columns = Math.Max(1, (int)Math.Ceiling(available / 340));
        ProjectCardWidth = Math.Max(120, Math.Floor(available / columns) - 12);
    }
    private async void OpenProject(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CurseForgeViewModel model || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            if (Uri.TryCreate(model.Project?.Links.WebsiteUrl, UriKind.Absolute, out var uri) && CurseForgeClient.IsProjectUri(uri))
                await top.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex) { model.Error = ex.Message; }
    }
    private async void ChooseDownloadFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CurseForgeViewModel model || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new() { Title = model.Text["CfManualChooseFolder"], AllowMultiple = false });
            try { if (model.IsManualDownload && folders.FirstOrDefault()?.TryGetLocalPath() is { } path) model.DownloadFolder = path; }
            finally { foreach (var folder in folders) folder.Dispose(); }
        }
        catch (Exception ex) { model.ManualBrowserError = ex.Message; }
    }
}

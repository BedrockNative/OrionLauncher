using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Orion.Desktop.ViewModels;

namespace Orion.Desktop.Views;

public partial class ContentLibraryPanel : UserControl
{
    public ContentLibraryPanel() => InitializeComponent();
    private void LibraryLayoutSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Grid grid)
            grid.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(e.NewSize.Width * .3, 240, 360));
    }
    private async void ImportFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ContentLibraryViewModel model || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = model.Text["ContentImport"], AllowMultiple = false,
                FileTypeFilter = [new("Minecraft Bedrock") { Patterns = ["*.mcaddon", "*.mcpack", "*.mcworld"] }] });
            try { if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await model.ImportAsync(path); }
            finally { foreach (var file in files) file.Dispose(); }
        }
        catch (Exception ex) { model.Error = ex.Message; }
    }
    private async void ManageInstance(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ContentTargetViewModel target } || TopLevel.GetTopLevel(this) is not MainWindow { DataContext: MainViewModel main } window) return;
        await main.ManageContentAsync(target.Instance.Id, (model, ct) => new ContentWindow(model).OpenAsync(window, ct));
        if (DataContext is ContentLibraryViewModel library) await library.RefreshAsync();
    }
}

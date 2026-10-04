using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Orion.Desktop.ViewModels;
using Orion.Domain;

namespace Orion.Desktop.Views;

public partial class ContentWindow : Window
{
    private ContentManagementViewModel Model => (ContentManagementViewModel)DataContext!;
    private bool closing;
    private bool closedSafely;
    public ContentWindow() => InitializeComponent();
    public ContentWindow(ContentManagementViewModel model) : this()
    {
        DataContext = model;
        Opened += async (_, _) => { if (!closing) await model.RefreshAsync(); };
        Closing += async (_, e) => { if (!closedSafely) { e.Cancel = true; await CloseSafelyAsync(); } };
    }
    public async Task OpenAsync(Window owner, CancellationToken ct)
    {
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(async () => await CloseSafelyAsync()));
        await ShowDialog(owner);
    }
    private async Task CloseSafelyAsync()
    {
        if (closing) return; closing = true;
        await Model.StopAsync(); closedSafely = true; Close();
    }
    private async void CloseWindow(object? sender, RoutedEventArgs e) => await CloseSafelyAsync();
    private async void ImportFile(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = Model.Text["ContentImport"], AllowMultiple = false,
                FileTypeFilter = [new("Minecraft Bedrock") { Patterns = ["*.mcworld", "*.mcpack", "*.mcaddon"] }]
            });
            if (!closing && files.FirstOrDefault()?.TryGetLocalPath() is { } path) await Model.ImportAsync(path);
            foreach (var file in files) file.Dispose();
        }
        catch (Exception ex) { Model.Error = ex.Message; }
    }
    private async void ExportFile(object? sender, RoutedEventArgs e)
    {
        if (Model.Selected is not { } row) return;
        try
        {
            var extension = row.Entry.Kind == ContentKind.World ? "mcworld" : "mcpack";
            using var file = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = Model.Text["ContentExport"], SuggestedFileName = "orion-export." + extension,
                DefaultExtension = extension, FileTypeChoices = [new("Minecraft Bedrock") { Patterns = ["*." + extension] }]
            });
            if (!closing && file?.TryGetLocalPath() is { } path) await Model.ExportAsync(path);
        }
        catch (Exception ex) { Model.Error = ex.Message; }
    }
    private async void OpenFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Model.SelectedFolder() is { } path)
            {
                if (Owner is MainWindow main) await main.OpenFolderAsync(path);
                else await new Orion.Infrastructure.Linux.DesktopFolderLauncher().OpenAsync(path, "system",
                    ex => Dispatcher.UIThread.Post(() => Model.Error = ex.Message));
            }
        }
        catch (Exception ex) { Model.Error = ex.Message; }
    }
}

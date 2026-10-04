using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Orion.Desktop.ViewModels;

namespace Orion.Desktop.Views;

public partial class RtxPanel : UserControl
{
    private RtxViewModel? bound;
    public RtxPanel()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Bind(); DataContextChanged += (_, _) => Bind();
        DetachedFromVisualTree += (_, _) => Unbind(); SizeChanged += (_, _) => Layout();
    }
    private void Unbind() { if (bound is not null) bound.PropertyChanged -= Changed; bound = null; }
    private void Bind() { Unbind(); bound = DataContext as RtxViewModel; if (bound is not null) bound.PropertyChanged += Changed; Layout(); }
    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(RtxViewModel.HasPreset)) Layout(); }
    private void Layout()
    {
        if (Results is null || Details is null) return;
        var selected = bound?.HasPreset == true; var narrow = Bounds.Width < 860;
        Results.IsVisible = !selected || !narrow; Grid.SetColumnSpan(Results, selected ? 1 : 2);
        Grid.SetColumn(Details, narrow ? 0 : 1); Grid.SetColumnSpan(Details, narrow ? 2 : 1);
    }
    private async void Import(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RtxViewModel model || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = model.Text["RtxImport"], AllowMultiple = false,
                FileTypeFilter = [new("BetterRTX") { Patterns = ["*.rtpack"] }] });
            try { if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await model.ImportAsync(path); }
            finally { foreach (var file in files) file.Dispose(); }
        }
        catch (Exception ex) { model.Error = ex.Message; }
    }
    private async void ImportMaterials(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RtxViewModel model || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = model.Text["RtxImportMaterials"], AllowMultiple = true,
                FileTypeFilter = [new("BetterRTX materials") { Patterns = ["*.material.bin"] }] });
            try
            {
                if (files.Count == 0) return;
                var paths = files.Select(f => f.TryGetLocalPath() ?? throw new InvalidOperationException("Choose local material files.")).ToArray();
                await model.ImportMaterialsAsync(paths);
            }
            finally { foreach (var file in files) file.Dispose(); }
        }
        catch (Exception ex) { model.Error = ex.Message; }
    }
    private async void ExportCurrent(object? sender, RoutedEventArgs e) => await Export(false);
    private async void ExportOriginal(object? sender, RoutedEventArgs e) => await Export(true);
    private async Task Export(bool original)
    {
        if (DataContext is not RtxViewModel model || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new() { Title = model.Text["RtxExport"], AllowMultiple = false });
            try { if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) await model.ExportAsync(path, original); }
            finally { foreach (var folder in folders) folder.Dispose(); }
        }
        catch (Exception ex) { model.Error = ex.Message; }
    }
    private async void ImportDlss(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RtxViewModel model || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = model.Text["RtxDlssLocal"], AllowMultiple = false,
                FileTypeFilter = [new("DLSS x64") { Patterns = ["nvngx_dlss.dll"] }] });
            try { if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await model.ImportDlssAsync(path); }
            finally { foreach (var file in files) file.Dispose(); }
        }
        catch (Exception ex) { model.Error = ex.Message; }
    }
    private async void ExportCreatorSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RtxViewModel model || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new() { Title = model.Text["RtxCreatorExport"], AllowMultiple = false });
            try { if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) await model.ExportCreatorSettingsAsync(path); }
            finally { foreach (var folder in folders) folder.Dispose(); }
        }
        catch (Exception ex) { model.Error = ex.Message; }
    }
}

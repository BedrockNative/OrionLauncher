using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Theming;
using Orion.Infrastructure.Linux;

namespace Orion.Desktop.Views;

public partial class AdvancedAppearancePanel : UserControl
{
    private bool changingOpacity;
    public AdvancedAppearancePanel()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => RefreshOpacityState();
    }
    private static CompositorOpacityRule? OpacityRule()
    {
        if (DesktopTransparency.Detect().Id != "umbriel") return null;
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config) || !Path.IsPathFullyQualified(config))
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return new(Path.Combine(config, "umbriel", "config.toml"));
    }
    private void RefreshOpacityState()
    {
        if (DataContext is not AdvancedAppearanceViewModel model) return;
        try { model.KeepWindowOpaque = OpacityRule()?.IsInstalled == true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { model.KeepWindowOpaque = false; }
    }
    private async void ChangeDesktopOpacity(object? sender, RoutedEventArgs e)
    {
        if (changingOpacity || DataContext is not AdvancedAppearanceViewModel model || TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        var requested = DesktopOpacityToggle.IsChecked == true;
        DesktopOpacityToggle.SetCurrentValue(CheckBox.IsCheckedProperty, model.KeepWindowOpaque);
        changingOpacity = true; DesktopOpacityToggle.IsEnabled = false;
        try
        {
            var rule = OpacityRule();
            if (rule is null)
            {
                model.KeepWindowOpaque = false;
                await owner.ConfirmAsync(model.Text["DesktopTransparency"], model.DesktopEnvironmentName + "\n\n" + model.Text["DesktopRuleRequired"] + "\n\n" + model.DesktopOpacityGuide,
                    model.Text["Close"], model.Text["Cancel"]);
                return;
            }
            var message = (requested ? model.Text["DesktopRuleConsent"] : model.Text["DesktopRuleUndoConsent"]) + "\n\n" + rule.ConfigPath;
            if (!await owner.ConfirmAsync(model.Text["DesktopTransparency"], message, model.Text[requested ? "DesktopRuleApply" : "DesktopRuleRemove"], model.Text["Cancel"])) return;
            await rule.SetEnabledAsync(requested);
            model.KeepWindowOpaque = rule.IsInstalled;
            model.DesktopOpacityStatus = model.Text[requested ? "DesktopRuleApplied" : "DesktopRuleRemoved"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException or ArgumentException)
        {
            RefreshOpacityState();
            model.DesktopOpacityStatus = model.Text["DesktopRuleFailed"];
            await owner.ConfirmAsync(model.Text["DesktopTransparency"], model.Text["DesktopRuleFailed"] + "\n\n" + ex.Message, model.Text["Close"], model.Text["Cancel"]);
        }
        finally
        {
            DesktopOpacityToggle.SetCurrentValue(CheckBox.IsCheckedProperty, model.KeepWindowOpaque);
            DesktopOpacityToggle.IsEnabled = true; changingOpacity = false;
        }
    }
    private async void CopyDesktopRule(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AdvancedAppearanceViewModel model || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(model.DesktopOpacityGuide); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { /* The selectable guide remains available when the clipboard is unavailable. */ }
    }
    private async void ChooseImage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AdvancedAppearanceViewModel model || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new()
            {
                Title = model.Text["ChooseWallpaperFile"], AllowMultiple = false,
                FileTypeFilter = [new("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif"] }]
            });
            try { if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) model.ChooseWallpaperFile(path); }
            finally { foreach (var file in files) file.Dispose(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { model.WallpaperStatus = model.Text["WallpaperFailed"]; }
    }
    private async void ChooseFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AdvancedAppearanceViewModel model || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new() { Title = model.Text["ChooseWallpaperFolder"], AllowMultiple = false });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) model.ChooseWallpaperDirectory(path);
            foreach (var folder in folders) folder.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { model.WallpaperStatus = model.Text["WallpaperFailed"]; }
    }
}

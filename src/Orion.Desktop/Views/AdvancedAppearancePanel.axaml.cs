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
    private readonly Func<CompositorOpacityRule?> opacityRule;
    private CompositorOpacityRule? pendingRule;
    private bool pendingOpacity;
    public AdvancedAppearancePanel() : this(OpacityRule) { }
    public AdvancedAppearancePanel(Func<CompositorOpacityRule?> opacityRule)
    {
        this.opacityRule = opacityRule;
        InitializeComponent();
        AttachedToVisualTree += (_, _) => RefreshOpacityState();
        DetachedFromVisualTree += (_, _) => { if (!changingOpacity) ResetOpacityConfirmation(); };
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
        try { model.KeepWindowOpaque = opacityRule()?.IsInstalled == true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { model.KeepWindowOpaque = false; }
    }
    private void ChangeDesktopOpacity(object? sender, RoutedEventArgs e)
    {
        if (changingOpacity || DataContext is not AdvancedAppearanceViewModel model) return;
        var requested = DesktopOpacityToggle.IsChecked == true;
        DesktopOpacityToggle.SetCurrentValue(CheckBox.IsCheckedProperty, model.KeepWindowOpaque);
        try
        {
            pendingRule = opacityRule(); pendingOpacity = requested;
            OpacityConfirmButton.IsVisible = pendingRule is not null;
            OpacityConfirmButton.Content = model.Text[requested ? "DesktopRuleApply" : "DesktopRuleRemove"];
            OpacityConfirmationText.Text = pendingRule is null
                ? model.DesktopEnvironmentName + "\n\n" + model.Text["DesktopRuleRequired"] + "\n\n" + model.DesktopOpacityGuide
                : model.Text[requested ? "DesktopRuleConsent" : "DesktopRuleUndoConsent"] + "\n\n" + pendingRule.ConfigPath;
            OpacityConfirmation.IsVisible = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException or ArgumentException)
        {
            ResetOpacityConfirmation();
            model.DesktopOpacityStatus = model.Text["DesktopRuleFailed"] + " " + ex.Message;
        }
    }
    private void ResetOpacityConfirmation()
    {
        pendingRule = null; OpacityConfirmation.IsVisible = false;
    }
    private void CancelDesktopOpacity(object? sender, RoutedEventArgs e)
    {
        if (!changingOpacity) ResetOpacityConfirmation();
    }
    private async void ApplyDesktopOpacity(object? sender, RoutedEventArgs e)
    {
        if (changingOpacity || pendingRule is not { } rule || DataContext is not AdvancedAppearanceViewModel model) return;
        var requested = pendingOpacity;
        changingOpacity = true;
        DesktopOpacityToggle.IsEnabled = OpacityConfirmButton.IsEnabled = OpacityCancelButton.IsEnabled = false;
        try
        {
            // Validation, file reads and writes must never hold the UI dispatcher.
            var installed = await Task.Run(async () => { await rule.SetEnabledAsync(requested); return rule.IsInstalled; });
            model.KeepWindowOpaque = installed;
            model.DesktopOpacityStatus = model.Text[requested ? "DesktopRuleApplied" : "DesktopRuleRemoved"];
            ResetOpacityConfirmation();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException or ArgumentException)
        {
            model.DesktopOpacityStatus = model.Text["DesktopRuleFailed"] + " " + ex.Message;
        }
        finally
        {
            DesktopOpacityToggle.SetCurrentValue(CheckBox.IsCheckedProperty, model.KeepWindowOpaque);
            DesktopOpacityToggle.IsEnabled = OpacityConfirmButton.IsEnabled = OpacityCancelButton.IsEnabled = true;
            changingOpacity = false;
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

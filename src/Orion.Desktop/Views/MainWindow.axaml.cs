using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Orion.Desktop.ViewModels;

namespace Orion.Desktop.Views;

public partial class MainWindow : Window, IWindowDialogs
{
    private double requestedLogHeight = 240;
    private readonly Dictionary<string, InstanceLogWindow> logWindows = new(StringComparer.Ordinal);
    public IReadOnlyCollection<InstanceLogWindow> LogWindows => logWindows.Values;
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindAppearance();
        LayoutUpdated += (_, _) => ConstrainLog();
        Closed += (_, _) => { CloseLogWindows(); (DataContext as MainViewModel)?.DisposeProfiles(); UnbindAppearance(); };
    }
    private void UnbindAppearance()
    {
        Orion.Desktop.Theming.ThemeManager.Changed -= MotionChanged;
    }
    private void BindAppearance()
    {
        UnbindAppearance();
        if (DataContext is not MainViewModel model) return;
        Orion.Desktop.Theming.ThemeManager.Changed += MotionChanged;
        Wallpaper.Bind(model.Appearance.Advanced); MotionChanged();
    }
    private void MotionChanged() => Classes.Set("reducedMotion", Orion.Desktop.Theming.ThemeManager.Current.ReduceMotion);

    public async Task DetachLogAsync(InstanceLogViewModel source)
    {
        if (source.LogPath is not { } path) return;
        if (logWindows.TryGetValue(path, out var existing)) { existing.Activate(); source.Close(); return; }
        var log = new InstanceLogViewModel(source.Text, isDetached: true) { Follow = source.Follow };
        await log.OpenAsync(source.Name, path);
        // A second click can arrive while the first snapshot is being read.
        if (logWindows.TryGetValue(path, out existing))
        {
            log.Close(); existing.Activate();
            if (source.LogPath == path) source.Close();
            return;
        }
        var window = new InstanceLogWindow(log);
        logWindows.Add(path, window);
        window.Closed += (_, _) => logWindows.Remove(path);
        window.Show();
        if (source.LogPath == path) source.Close();
    }

    public void CloseLogWindows()
    {
        foreach (var window in logWindows.Values.ToArray()) window.Close();
    }

    public static double MaximumLogHeight(double available, double firstRowBottom) => Math.Max(0, available - firstRowBottom - 12);
    public async Task OpenWebLinkAsync(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !await Launcher.LaunchUriAsync(uri))
            throw new IOException("Could not open the browser.");
    }
    public Task ShowChangelogAsync(Orion.Desktop.I18n.Localizer text, string language) =>
        new ChangelogWindow(new(text, language)).ShowDialog(this);

    private void ConstrainLog()
    {
        if (!LogDock.IsVisible)
        {
            LibraryItems.Margin = new Thickness(0);
            MainContent.Margin = new Thickness(36, 32, 36, 20);
            return;
        }
        var firstRowBottom = DataContext is MainViewModel { IsLibrary: true }
            ? (LibraryScroll.TranslatePoint(new Point(0, 0), this)?.Y ?? 185) + 276 : 180;
        var maximum = MaximumLogHeight(Bounds.Height - 44, firstRowBottom);
        LogDock.MaxHeight = maximum;
        LogDock.Height = Math.Clamp(requestedLogHeight, Math.Min(140, maximum), maximum);
        LibraryItems.Margin = new Thickness(0, 0, 0, LogDock.Height);
        MainContent.Margin = new Thickness(36, 32, 36, DataContext is MainViewModel { IsLibrary: true } ? 20 : 20 + LogDock.Height);
    }

    private void ResizeLog(object? sender, VectorEventArgs e)
    {
        requestedLogHeight = Math.Clamp(LogDock.Height - e.Vector.Y, Math.Min(140, LogDock.MaxHeight), LogDock.MaxHeight);
        ConstrainLog();
    }

    public Task<bool> ConfirmAsync(string title, string text, string confirm, string cancel)
        => ConfirmOwnedAsync(this, title, text, confirm, cancel);

    internal static async Task<bool> ConfirmOwnedAsync(Window owner, string title, string text, string confirm, string cancel)
    {
        var content = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 420 };
        var dialog = Dialog(title, content, confirm, cancel, out var accept, out var dismiss);
        accept.Click += (_, _) => dialog.Close(true);
        dismiss.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(owner);
    }

    private static Window Dialog(string title, Control content, string confirm, string cancel, out Button accept, out Button dismiss)
    {
        accept = new() { Content = confirm, Classes = { "primary" } };
        dismiss = new() { Content = cancel };
        return new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(28), Spacing = 22,
                Children = { new TextBlock { Text = title, FontSize = 22 }, content,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 12, Children = { dismiss, accept } } }
            }
        };
    }

    public async Task OpenFolderAsync(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (!directory.Exists) throw new DirectoryNotFoundException(directory.FullName);
        if (!await Launcher.LaunchDirectoryInfoAsync(directory))
            throw new IOException($"Could not open the file manager: {directory.FullName}");
    }

    public async Task<string?> ChooseFolderAsync(string title, string hint, IReadOnlyList<FolderChoice> choices, string confirm, string cancel)
    {
        var picker = new ComboBox { ItemsSource = choices, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var preview = new TextBlock { Text = choices.FirstOrDefault()?.Path, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 };
        picker.SelectionChanged += (_, _) => preview.Text = (picker.SelectedItem as FolderChoice)?.Path;
        var content = new StackPanel { Width = 480, Spacing = 12, Children = {
            new TextBlock { Text = hint, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, picker, preview } };
        var dialog = Dialog(title, content, confirm, cancel, out var accept, out var dismiss);
        accept.Click += (_, _) => dialog.Close((picker.SelectedItem as FolderChoice)?.Path);
        dismiss.Click += (_, _) => dialog.Close(null);
        return await dialog.ShowDialog<string?>(this);
    }
}

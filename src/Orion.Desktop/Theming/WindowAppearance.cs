using Avalonia;
using Avalonia.Controls;
using Orion.Desktop.Views;

namespace Orion.Desktop.Theming;

/// <summary>Use the same wallpaper in owned windows without decoding a copy per dialog.</summary>
public sealed class WindowAppearance : AvaloniaObject
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<WindowAppearance, Window, bool>("Enabled");
    public static bool GetEnabled(Window window) => window.GetValue(EnabledProperty);
    public static void SetEnabled(Window window, bool value) => window.SetValue(EnabledProperty, value);
    static WindowAppearance()
    {
        EnabledProperty.Changed.AddClassHandler<Window>((window, args) =>
        {
            if (args.NewValue is not true) return;
            X11Properties.SetWmClass(window, "OrionLauncher");
            var initialized = false;
            window.Opened += (_, _) =>
            {
                if (initialized) return;
                initialized = true;
                if (window is not MainWindow && window.Content is Control content && content.Name != "AppearanceRoot")
                {
                    window.Content = null;
                    window.Content = new Grid { Name = "AppearanceRoot", Children = { new WallpaperView { IsMirror = true }, content } };
                }
                var handle = window.TryGetPlatformHandle();
                var opacity = OperatingSystem.IsLinux() && handle is { HandleDescriptor: "XID", Handle: not 0 }
                    ? new X11WindowOpacity(handle.Handle) : null;
                void Apply()
                {
                    window.Classes.Set("reducedMotion", ThemeManager.Current.ReduceMotion);
                    // The wallpaper and translucent controls are composed inside this opaque surface.
                    window.TransparencyLevelHint = [WindowTransparencyLevel.None];
                    window.Opacity = 1;
                    if (opacity is not null) _ = opacity.SetEnabledAsync(ThemeManager.Current.KeepWindowOpaque);
                }
                Apply(); ThemeManager.Changed += Apply;
                window.Closed += (_, _) => { ThemeManager.Changed -= Apply; opacity?.Dispose(); };
            };
        });
    }
}

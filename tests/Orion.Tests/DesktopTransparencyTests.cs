using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Orion.Desktop.I18n;
using Orion.Desktop.Theming;
using Orion.Desktop.ViewModels;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class DesktopTransparencyTests
{
    [Theory]
    [InlineData("umbriel", "umbriel")]
    [InlineData("Hyprland", "hyprland")]
    [InlineData("niri", "niri")]
    [InlineData("KDE", "kde")]
    [InlineData("ubuntu:GNOME", "gnome")]
    [InlineData("zorin:GNOME", "gnome")]
    [InlineData("pop:GNOME", "gnome")]
    [InlineData("COSMIC", "cosmic")]
    [InlineData("other", "other")]
    public void DetectsSessionWithoutExecutingDesktopCommands(string desktop, string expected)
    {
        Assert.Equal(expected, DesktopTransparency.Detect(key => key == "XDG_CURRENT_DESKTOP" ? desktop : null).Id);
        Assert.Equal("umbriel", DesktopTransparency.Detect(key => key switch
        {
            "XDG_CURRENT_DESKTOP" => "umbriel", "HYPRLAND_INSTANCE_SIGNATURE" => "stale", _ => null
        }).Id);
    }

    [Fact]
    public async Task PreferenceDefaultsOffPersistsAndRestoresWithoutChangingInternalMaterials()
    {
        using var directory = new TestDirectory();
        var store = new SettingsStore(directory.Paths);
        Assert.False((await store.LoadAsync()).Appearance.KeepWindowOpaque);
        var model = new AdvancedAppearanceViewModel(new(), new Localizer());
        var changes = 0; model.Changed += () => changes++;
        model.KeepWindowOpaque = true;
        Assert.True(changes > 0);
        var settings = new LauncherSettings { Appearance = model.Snapshot() };
        await store.SaveAsync(settings);
        Assert.True((await store.LoadAsync()).Appearance.KeepWindowOpaque);
        model.Restore(new());
        Assert.False(model.KeepWindowOpaque);
        model.Restore(settings.Appearance);
        Assert.True(model.KeepWindowOpaque);
        Assert.Equal(settings.Appearance.Dark, model.Dark.Snapshot());
    }

    [Fact]
    public async Task OpacityRequestsAreScopedSerializedAndRestoreOriginalValue()
    {
        var calls = new List<string>();
        uint? value = 1234;
        Task<string> Execute(IReadOnlyList<string> args, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Assert.Equal("-id", args[0]); Assert.Equal("0x123", args[1]);
            calls.Add(string.Join(' ', args));
            if (args.Count == 3) return Task.FromResult(value is null ? "_NET_WM_WINDOW_OPACITY: not found." : $"_NET_WM_WINDOW_OPACITY(CARDINAL) = {value}");
            value = args.Contains("-remove") ? null : uint.Parse(args[^1]);
            return Task.FromResult("");
        }
        using var request = new X11WindowOpacity(0x123, Execute);
        await request.SetEnabledAsync(false); Assert.Empty(calls);
        await request.SetEnabledAsync(true); Assert.Equal(uint.MaxValue, value);
        var count = calls.Count;
        await request.SetEnabledAsync(true); Assert.Equal(count, calls.Count);
        await request.SetEnabledAsync(false); Assert.Equal(1234u, value);
        value = null;
        var on = request.SetEnabledAsync(true);
        var off = request.SetEnabledAsync(false);
        await Task.WhenAll(on, off);
        Assert.Null(value);
        await request.SetEnabledAsync(true);
        value = 99; // Another tool changed the window after Orion's request.
        await request.SetEnabledAsync(false);
        Assert.Equal(99u, value);
    }

    [Fact]
    public async Task MissingHelperAndClosedDisplayDoNotCrashAppearanceChanges()
    {
        using var request = new X11WindowOpacity(123, (_, _) => throw new System.ComponentModel.Win32Exception());
        await request.SetEnabledAsync(true);
        await request.SetEnabledAsync(false);
        Assert.Null(X11WindowOpacity.ParseValue("_OTHER(CARDINAL) = 1"));
        Assert.Null(X11WindowOpacity.ParseValue("_NET_WM_WINDOW_OPACITY(CARDINAL) = 4294967296"));
        Assert.Equal(uint.MaxValue, X11WindowOpacity.ParseValue("_NET_WM_WINDOW_OPACITY(CARDINAL) = 4294967295\n"));
    }

    [Fact]
    public async Task DialogsKeepOpaqueBaseAndTranslucentInternalPanels()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(() =>
        {
            var appearance = new AppearanceSettings { Dark = new() { SurfaceOpacity = 35, ProtectReadability = false } };
            ThemeManager.Apply("dark", "mint", "orion", appearance);
            var window = new Window { Content = new Border { Classes = { "panel" }, Width = 200, Height = 100 } };
            try
            {
                window.Show();
                Assert.Equal("OrionLauncher", X11Properties.GetWmClass(window));
                Assert.Equal(WindowTransparencyLevel.None, Assert.Single(window.TransparencyLevelHint));
                Assert.Equal(1d, window.Opacity);
                Assert.Equal(255, Assert.IsType<SolidColorBrush>(window.Background).Color.A);
                var resources = (ResourceDictionary)Avalonia.Application.Current!.Resources.ThemeDictionaries[ThemeVariant.Dark];
                var panel = (SolidColorBrush)resources["OrionMaterialBrush"]!;
                Assert.InRange(panel.Color.A, 88, 90);
                ThemeManager.Apply("dark", "mint", "orion", appearance with { KeepWindowOpaque = false });
                Assert.Equal(255, Assert.IsType<SolidColorBrush>(window.Background).Color.A);
                Assert.Equal(WindowTransparencyLevel.None, Assert.Single(window.TransparencyLevelHint));
            }
            finally { window.Close(); ThemeManager.Apply("dark", "mint"); }
            return Task.FromResult(true);
        }, CancellationToken.None);
    }
}

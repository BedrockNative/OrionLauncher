using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orion.Desktop.Composition;
using Orion.Desktop.I18n;
using Orion.Desktop.Theming;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Views;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class AdvancedAppearanceTests
{
    [Fact]
    public void CatalogAndGeneratedCustomAccentsKeepContrastOnEverySurfaceSet()
    {
        Assert.Equal(49, AccentCatalog.All.Count);
        Assert.Equal(49, AccentCatalog.All.Select(c => c.Id).Distinct().Count());
        Assert.Equal(5, ThemeManager.Modes.Count);
        foreach (var palette in AccentCatalog.All.Concat(new[] { Colors.Black, Colors.White, Colors.Red, Colors.Lime, Colors.Blue }.Select(c => AccentCatalog.FromColor("custom", "Custom", c))))
        foreach (var light in new[] { false, true })
        foreach (var theme in VisualThemes.All)
        foreach (var baseMode in new[] { "original", "none", "theme", "color" })
        {
            var resources = ThemeManager.CreateResources(palette, light, theme, new() { BaseMode = baseMode, BaseColor = "#FF00FF", BaseOpacity = 100 });
            Color Get(string key) => ((SolidColorBrush)resources["Orion" + key + "Brush"]!).Color;
            foreach (var (fg, bg) in new[] { ("Text", "Panel"), ("Muted", "Panel"), ("Muted", "Sidebar"), ("Muted", "Inset"), ("Accent", "Selection"), ("Accent", "Status"), ("OnAccent", "Accent") })
                Assert.True(ThemeManager.Contrast(Get(fg), Get(bg)) >= 4.5, $"{palette.Id}/{light}/{theme.Id}/{baseMode}: {fg}/{bg}");
        }
    }
    [Theory]
    [InlineData(1200,1200,420,true)] [InlineData(419,1200,420,true)] [InlineData(420,1200,420,false)]
    [InlineData(700,600,800,true)] [InlineData(800,600,800,false)] [InlineData(700,600,600,true)]
    public void SchedulesSupportOvernightAndBoundaryMinutes(int minute, int start, int end, bool dark) => Assert.Equal(dark, ThemeClock.IsNight(minute, start, end));
    [Fact]
    public void SolarModeHandlesLocalOffsetsAndPolarDayAndNight()
    {
        var noon = new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.Zero);
        Assert.True(ThemeClock.SunIsUp(noon, 0, 0)); Assert.False(ThemeClock.SunIsUp(noon.AddHours(12), 0, 0));
        Assert.Equal(ThemeClock.SunIsUp(noon, -23, -46), ThemeClock.SunIsUp(noon.ToOffset(TimeSpan.FromHours(-3)), -23, -46));
        Assert.True(ThemeClock.SunIsUp(new(2026, 6, 21, 0, 0, 0, TimeSpan.Zero), 89, 0));
        Assert.False(ThemeClock.SunIsUp(new(2026, 12, 21, 12, 0, 0, TimeSpan.Zero), 89, 0));
    }
    [Fact]
    public async Task SettingsNormalizeCorruptionAndRoundTripIndependentProfiles()
    {
        var bad = new AppearanceSettings { Latitude = double.NaN, WallpaperBlur = double.PositiveInfinity, WallpaperOpacity = -90,
            WallpaperFit = "invalid", WallpaperDirectory = "relative", Light = null!, Dark = new() { CustomAccent = "invalid", SurfaceBlur = 1000 } }.Normalize();
        Assert.Equal(0, bad.Latitude); Assert.Equal(0, bad.WallpaperBlur); Assert.Equal(0, bad.WallpaperOpacity);
        Assert.Equal("smart", bad.WallpaperFit); Assert.Equal("", bad.WallpaperDirectory); Assert.Equal(32, bad.Dark.SurfaceBlur);
        using var directory = new TestDirectory();
        var store = new SettingsStore(directory.Paths);
        var settings = new LauncherSettings { Appearance = new() { TopNavigation = true, Light = new() { Accent = "hue_emerald" }, Dark = new() { Accent = "custom", CustomAccent = "#FF0000" } } };
        await store.SaveAsync(settings); Assert.Equal(settings, await store.LoadAsync());
    }
    [Fact]
    public async Task AdvancedPreviewRevertAndNarrowTopNavigationRender()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            await using var services = new LauncherServices(directory.Paths, new("pt-BR"), ["orion"]);
            var window = new MainWindow { Width = 900, Height = 640 }; var model = new MainViewModel(services, window); window.DataContext = model;
            await model.InitializeAsync(); window.Show(); model.Page = "settings";
            Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "LauncherSettingsTabs").SelectedIndex = 1;
            var appearance = model.Appearance;
            appearance.Advanced.TopNavigation = true; appearance.Advanced.ReduceMotion = true;
            appearance.Advanced.Dark.AccentIndex = 1; appearance.Advanced.Dark.CustomAccent = "#CF3481";
            appearance.Advanced.Dark.MaterialCommand.Execute("clear");
            Assert.True(appearance.IsDirty); Assert.Contains("reducedMotion", window.Classes);
            Assert.Equal(7, appearance.Advanced.Fits.Count);
            Assert.NotEqual(appearance.Advanced.Dark.Snapshot(), appearance.Advanced.Light.Snapshot());
            var persisted = new LauncherSettings { Appearance = appearance.Advanced.Snapshot() };
            appearance.AcceptChanges(persisted);
            appearance.Advanced.Dark.CustomAccent = "#FFFFFF"; appearance.RevertCommand.Execute(null);
            Assert.Equal(persisted.Appearance, appearance.Advanced.Snapshot());
            Assert.False(appearance.IsDirty); Assert.Equal("#CF3481", appearance.Advanced.Dark.CustomAccent);
            appearance.Advanced.EditLight = true; Dispatcher.UIThread.RunJobs();
            appearance.Advanced.EditLight = false; Dispatcher.UIThread.RunJobs();
            Assert.Equal("custom", appearance.Advanced.Dark.Snapshot().Accent);
            // MainViewModel auto-saves asynchronously; don't assert a saved state mid-write.
            await model.SaveSettingsCommand.ExecuteAsync(null);
            Assert.False(appearance.IsDirty);
            appearance.ModeIndex = 3; appearance.Advanced.DarkStart = TimeSpan.FromHours(18);
            using (var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(32, 32)))
            {
                surface.Canvas.Clear(SkiaSharp.SKColors.CornflowerBlue);
                using var image = surface.Snapshot(); using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(directory.Root, "wallpaper.png"), data.ToArray());
            }
            appearance.Advanced.WallpaperDirectory = directory.Root;
            for (var attempt = 0; attempt < 30 && !appearance.Advanced.WallpaperStatus.Contains("wallpaper.png"); attempt++) await Task.Delay(100);
            Assert.Contains("wallpaper.png", appearance.Advanced.WallpaperStatus);
            appearance.Advanced.WallpaperDirectory = Path.Combine(directory.Root, "missing");
            appearance.Advanced.WallpaperDirectory = directory.Root;
            await Task.Delay(200);
            Assert.Contains("wallpaper.png", appearance.Advanced.WallpaperStatus);
            appearance.Advanced.WallpaperBlur = 10;
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "ThemeSettingsScroll");
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
            using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
            if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
            {
                Directory.CreateDirectory(output); bitmap.Save(Path.Combine(output, "appearance-advanced.png"));
                foreach (var (offset, name) in new[] { (1000d, "profiles"), (scroll.Extent.Height, "wallpaper") })
                {
                    scroll.Offset = new(0, offset);
                    Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, $"appearance-{name}.png"));
                }
            }
            appearance.ResetCommand.Execute(null); Assert.False(appearance.Advanced.TopNavigation);
            Assert.Equal("", appearance.Advanced.WallpaperDirectory);
            await model.StopAllAsync(); window.Close(); return true;
        }, CancellationToken.None);
    }

    [Fact]
    public void WallpaperScanIsNonRecursiveAndRejectsLinksAndUnsupportedFiles()
    {
        using var directory = new TestDirectory();
        var allowed = Path.Combine(directory.Root, "allowed.PNG"); File.WriteAllText(allowed, "test");
        File.WriteAllText(Path.Combine(directory.Root, "ignored.txt"), "test");
        File.CreateSymbolicLink(Path.Combine(directory.Root, "linked.png"), allowed);
        var nested = Directory.CreateDirectory(Path.Combine(directory.Root, "nested"));
        File.WriteAllText(Path.Combine(nested.FullName, "ignored.png"), "test");
        Assert.Equal(new[] { allowed }, WallpaperView.Scan(directory.Root));
        Assert.Empty(WallpaperView.Scan(""));
    }
}

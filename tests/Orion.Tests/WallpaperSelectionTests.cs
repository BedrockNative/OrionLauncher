using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Orion.Desktop.I18n;
using Orion.Desktop.Theming;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Views;
using Orion.Infrastructure.Storage;
using SkiaSharp;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class WallpaperSelectionTests
{
    [Fact]
    public void FixedImageFolderAndRotationSettingsRoundTripAndNormalize()
    {
        var old = JsonSerializer.Deserialize<AppearanceSettings>("{\"WallpaperDirectory\":\"/tmp/wallpapers\"}")!.Normalize();
        Assert.False(old.RotateWallpapers); Assert.Equal("", old.WallpaperFile); Assert.Equal(5, old.WallpaperIntervalMinutes);
        var model = new AdvancedAppearanceViewModel(old, new Localizer());
        Assert.True(model.HasWallpaperFolder);
        model.RotateWallpapers = true; model.WallpaperIntervalMinutes = 12; model.RandomWallpaper = false;
        var roundTrip = JsonSerializer.Deserialize<AppearanceSettings>(JsonSerializer.Serialize(model.Snapshot()))!;
        model.Restore(roundTrip); Assert.True(model.RotateWallpapers); Assert.Equal(12, model.WallpaperIntervalMinutes);
        model.ChooseWallpaperFile("/tmp/fixed.png");
        Assert.Equal("/tmp/fixed.png", model.Snapshot().WallpaperFile); Assert.Equal("", model.WallpaperDirectory);
        Assert.False(model.RotateWallpapers); Assert.False(model.HasWallpaperFolder);
        model.ChooseWallpaperDirectory("/tmp/folder"); Assert.Equal("", model.WallpaperFile); Assert.True(model.HasWallpaperFolder);
        model.ClearWallpaperCommand.Execute(null); Assert.Equal("", model.WallpaperDirectory); Assert.Equal("", model.WallpaperFile);
        var invalid = new AppearanceSettings { WallpaperFile = "relative.png", WallpaperIntervalMinutes = -1 }.Normalize();
        Assert.Equal("", invalid.WallpaperFile); Assert.Equal(1, invalid.WallpaperIntervalMinutes);
        Assert.Equal(1440, (invalid with { WallpaperIntervalMinutes = int.MaxValue }).Normalize().WallpaperIntervalMinutes);
    }

    [Fact]
    public async Task FixedImageNeverAdvancesAndFolderTimerStopsWhenDisabledOrDetached()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var dir = new TestDirectory();
            foreach (var name in new[] { "a.png", "b.png" })
            {
                using var surface = SKSurface.Create(new SKImageInfo(32, 32)); surface.Canvas.Clear(SKColors.CornflowerBlue);
                using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(dir.Root, name), data.ToArray());
            }
            using var model = new ThemeSettingsViewModel(new(), new Localizer());
            var view = new WallpaperView(); view.Bind(model.Advanced);
            var window = new Window { Width = 400, Height = 300, Content = view }; window.Show();
            var timer = (DispatcherTimer)typeof(WallpaperView).GetField("rotation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
            async Task Loaded(string name)
            {
                for (var i = 0; i < 100 && !model.Advanced.WallpaperStatus.Contains(name); i++) await Task.Delay(20);
                Assert.Contains(name, model.Advanced.WallpaperStatus);
            }
            try
            {
                model.Advanced.ChooseWallpaperFile(Path.Combine(dir.Root, "b.png")); await Loaded("b.png");
                Assert.False(timer.IsEnabled);
                model.Advanced.NextWallpaperCommand.Execute(null); await Task.Delay(150); Assert.Contains("b.png", model.Advanced.WallpaperStatus);
                model.Advanced.RandomWallpaper = false; model.Advanced.ChooseWallpaperDirectory(dir.Root); await Loaded("a.png");
                Assert.False(timer.IsEnabled);
                model.Advanced.RotateWallpapers = true; Dispatcher.UIThread.RunJobs(); Assert.True(timer.IsEnabled);
                Assert.Equal(TimeSpan.FromMinutes(5), timer.Interval);
                // Exercise the actual dispatcher timer without waiting five minutes in the test.
                timer.Interval = TimeSpan.FromMilliseconds(200); await Loaded("b.png");
                model.Advanced.RotateWallpapers = false; Assert.False(timer.IsEnabled);
                model.Advanced.NextWallpaperCommand.Execute(null); await Loaded("a.png");
                model.Advanced.WallpaperIntervalMinutes = 8; model.Advanced.RotateWallpapers = true;
                Assert.True(timer.IsEnabled); Assert.Equal(TimeSpan.FromMinutes(8), timer.Interval);
                model.Advanced.ChooseWallpaperFile(Path.Combine(dir.Root, "b.png")); await Loaded("b.png"); Assert.False(timer.IsEnabled);
                model.Advanced.ChooseWallpaperFile(Path.Combine(dir.Root, "missing.png"));
                for (var i = 0; i < 100 && model.Advanced.WallpaperStatus != model.Text["WallpaperFailed"]; i++) await Task.Delay(20);
                Assert.Equal(model.Text["WallpaperFailed"], model.Advanced.WallpaperStatus); Assert.False(timer.IsEnabled);
                model.Advanced.ChooseWallpaperDirectory(dir.Root); model.Advanced.RotateWallpapers = true; await Loaded("a.png");
                window.Close(); Assert.False(timer.IsEnabled);
            }
            finally { window.Close(); ThemeManager.Apply("dark", "theme", "orion"); }
            return true;
        }, CancellationToken.None);
    }
}

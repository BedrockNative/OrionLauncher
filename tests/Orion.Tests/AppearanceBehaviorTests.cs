using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orion.Desktop.Composition;
using Orion.Desktop.Theming;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Views;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class AppearanceBehaviorTests
{
    [Fact]
    public async Task WallpaperControlsChangePixelsAndSharedDialogsMenusAndEmptyStateFollowAppearance()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var dir = new TestDirectory();
            var pictures = Directory.CreateDirectory(Path.Combine(dir.Root, "wallpapers"));
            using (var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(120, 80)))
            {
                using var paint = new SkiaSharp.SKPaint();
                for (var y = 0; y < 8; y++) for (var x = 0; x < 12; x++)
                { paint.Color = new((byte)(x * 21), (byte)(y * 30), (byte)((x + y) % 2 * 255)); surface.Canvas.DrawRect(x * 10, y * 10, 10, 10, paint); }
                using var image = surface.Snapshot(); using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(pictures.FullName, "pattern.png"), data.ToArray());
            }
            await using var services = new LauncherServices(dir.Paths, new("pt-BR"), ["orion"]);
            var window = new MainWindow { Width = 900, Height = 740 }; var model = new MainViewModel(services, window); window.DataContext = model;
            await model.InitializeAsync(); window.Show(); model.Page = "content";
            await model.ContentLibrary.RefreshAsync();
            var appearance = model.Appearance.Advanced;
            appearance.Dark.MaterialCommand.Execute("balanced");
            Assert.False(appearance.Dark.ProtectReadability);
            appearance.Dark.SurfaceBlur = 0; appearance.Dark.OverlayOpacity = 0;
            appearance.RandomWallpaper = false; appearance.WallpaperDirectory = pictures.FullName;
            for (var i = 0; i < 40 && !appearance.WallpaperStatus.Contains("pattern.png"); i++) await Task.Delay(100);
            Assert.Contains("pattern.png", appearance.WallpaperStatus);
            string Capture(Window target, string? name = null)
            {
                Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var image = target.CaptureRenderedFrame(); Assert.NotNull(image);
                using var bytes = new MemoryStream(); image.Save(bytes);
                if (name is not null && Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
                { Directory.CreateDirectory(output); image.Save(Path.Combine(output, name + ".png")); }
                return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
            }
            var baseline = Capture(window, "content-empty-wallpaper");
            appearance.Dark.BaseIndex = 3; appearance.Dark.BaseColor = "#CC3300"; appearance.Dark.BaseOpacity = 100;
            Assert.True(appearance.Dark.IsTintedBase); Assert.NotEqual(baseline, Capture(window));
            appearance.Dark.BaseIndex = 0; Assert.False(appearance.Dark.IsTintedBase);
            var empty = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "LibraryEmptyState");
            Assert.True(empty.IsEffectivelyVisible); Assert.True(empty.Bounds.Width > 500);
            model.ContentLibrary.Search = "missing"; Assert.Equal(model.Text["ContentNoMatches"], model.ContentLibrary.EmptyTitle);
            model.ContentLibrary.ClearFiltersCommand.Execute(null); Assert.False(model.ContentLibrary.HasFilters);
            appearance.WallpaperOpacity = 0; Assert.NotEqual(baseline, Capture(window)); appearance.WallpaperOpacity = 100;
            appearance.Dark.OverlayOpacity = 60; Assert.NotEqual(baseline, Capture(window)); appearance.Dark.OverlayOpacity = 0;
            appearance.WallpaperBrightness = 30; await Task.Delay(400); Assert.NotEqual(baseline, Capture(window));
            appearance.WallpaperBrightness = 100; await Task.Delay(400);
            appearance.WallpaperBlur = 18; await Task.Delay(400); Assert.NotEqual(baseline, Capture(window));
            appearance.WallpaperBlur = 0; appearance.Dark.SurfaceBlur = 12; await Task.Delay(400); Assert.NotEqual(baseline, Capture(window));
            appearance.Dark.SurfaceBlur = 0; await Task.Delay(400);
            var fits = new HashSet<string>();
            for (var i = 0; i < appearance.Fits.Count; i++) { appearance.FitIndex = i; fits.Add(Capture(window)); }
            Assert.Equal(7, fits.Count); appearance.FitIndex = 0;
            var picker = new ComboBox { ItemsSource = new[] { "Primeira opção", "Segunda opção" }, SelectedIndex = 0 };
            var panel = new Border { Classes = { "panel" }, Margin = new(24), Child = picker };
            var dialog = new Window { Width = 500, Height = 350, Content = panel }; dialog.Show();
            var dialogBefore = Capture(dialog, "dialog-wallpaper");
            Assert.Single(dialog.GetVisualDescendants().OfType<WallpaperView>(), v => v.IsMirror);
            Assert.InRange(Assert.IsType<SolidColorBrush>(panel.Background).Color.A, (byte)140, (byte)160);
            appearance.Dark.SurfaceOpacity = 20;
            Assert.NotEqual(dialogBefore, Capture(dialog));
            Assert.InRange(Assert.IsType<SolidColorBrush>(panel.Background).Color.A, (byte)50, (byte)52);
            appearance.ReduceMotion = true; Assert.Contains("reducedMotion", dialog.Classes);
            picker.IsDropDownOpen = true; Capture(dialog, "menu-wallpaper");
            var popup = picker.GetVisualDescendants().OfType<Popup>().Single();
            Assert.True(popup.ShouldUseOverlayLayer);
            Assert.InRange(Assert.IsType<SolidColorBrush>(Assert.IsType<Border>(popup.Child).Background).Color.A, (byte)239, (byte)255);
            picker.IsDropDownOpen = false;
            var visible = Capture(dialog); window.Hide(); Assert.Equal(visible, Capture(dialog)); window.Show();
            appearance.Dark.ProtectReadability = true;
            Capture(dialog); Assert.True(Assert.IsType<SolidColorBrush>(panel.Background).Color.A >= 244);
            appearance.Dark.ProtectReadability = false;
            visible = Capture(dialog); appearance.ClearWallpaperCommand.Execute(null); await Task.Delay(400);
            Assert.NotEqual(visible, Capture(dialog));
            dialog.Close(); await model.StopAllAsync(); window.Close(); return true;
        }, CancellationToken.None);
    }
}

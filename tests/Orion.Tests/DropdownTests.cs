using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orion.Desktop.I18n;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Views;
using Orion.Desktop.Theming;
using Orion.Domain;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class DropdownTests
{
    [Fact]
    public async Task InstanceAccountCoverAndMonitorSelectorsDismissWithoutBlockingTheWindow()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(() =>
        {
            var model = new LaunchOptionsViewModel(GameInstance.Create("Selectors", "26.50", "Release"));
            model.SetMonitorResolutions([new("Display", new(1920, 1080))]);
            var fields = new LaunchOptionsFields { DataContext = model };
            var window = new Window { Width = 900, Height = 640, Content = fields };
            window.Show();
            try
            {
                foreach (var tab in new[] { 0, 3, 4 })
                {
                    model.SelectedTab = tab; Dispatcher.UIThread.RunJobs();
                    var combo = fields.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible);
                    Assert.True(DropdownBehavior.GetEnabled(combo));
                    combo.IsDropDownOpen = true; Dispatcher.UIThread.RunJobs();
                    Assert.True(combo.IsDropDownOpen);
                    combo.Focus(); window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                    Dispatcher.UIThread.RunJobs(); Assert.False(combo.IsDropDownOpen);
                    combo.IsDropDownOpen = true; Dispatcher.UIThread.RunJobs();
                    model.SelectedTab = 1; Dispatcher.UIThread.RunJobs();
                    Assert.False(combo.IsDropDownOpen);
                }
            }
            finally { window.Close(); }
            return Task.FromResult(true);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ScrolledAppearanceSelectorsDoNotMoveThePageOrLeaveInputOverlayBehind()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(() =>
        {
            using var model = new ThemeSettingsViewModel(new(), new Localizer());
            var panel = new ThemeSettingsPanel { DataContext = model };
            var window = new Window { Width = 900, Height = 640, Content = panel };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var scroll = panel.FindControl<ScrollViewer>("ThemeSettingsScroll")!;
                var pickers = panel.GetVisualDescendants().OfType<ComboBox>().ToArray();
                foreach (var picker in pickers)
                {
                    picker.BringIntoView(); Dispatcher.UIThread.RunJobs();
                    var before = scroll.Offset;
                    var page = panel.Bounds;
                    picker.IsDropDownOpen = true; Dispatcher.UIThread.RunJobs();
                    Assert.Equal(before, scroll.Offset);
                    Assert.Equal(page, panel.Bounds);
                    var popup = picker.GetVisualDescendants().OfType<Popup>().Single();
                    Assert.NotNull(popup.Child);
                    var listScroll = popup.Child!.GetVisualDescendants().OfType<ScrollViewer>().First();
                    listScroll.Offset = new Vector(0, 30); Dispatcher.UIThread.RunJobs();
                    Assert.True(picker.IsDropDownOpen);
                    listScroll.Offset = default; Dispatcher.UIThread.RunJobs();
                    if (picker.Name == "BackgroundBasePicker" && Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
                    {
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                        Directory.CreateDirectory(output); frame.Save(Path.Combine(output, "background-base-selector.png"));
                    }
                    var item = picker.ContainerFromIndex(Math.Min(1, picker.ItemCount - 1))!;
                    Assert.NotNull(item);
                    var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window);
                    Assert.NotNull(point);
                    window.MouseDown(point.Value, MouseButton.Left); window.MouseUp(point.Value, MouseButton.Left);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(picker.IsDropDownOpen);
                    Assert.False(popup.IsOpen);
                    Assert.Equal(before, scroll.Offset);
                    picker.IsDropDownOpen = true; Dispatcher.UIThread.RunJobs();
                    window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(picker.IsDropDownOpen);
                    picker.IsDropDownOpen = true; Dispatcher.UIThread.RunJobs();
                    scroll.Offset = new Vector(0, Math.Max(0, scroll.Offset.Y - 20));
                    Dispatcher.UIThread.RunJobs();
                    // A scroll invalidates the popup's anchor. It must not cover unrelated controls.
                    if (scroll.Offset != before) Assert.False(picker.IsDropDownOpen);
                    picker.IsDropDownOpen = true; Dispatcher.UIThread.RunJobs();
                    panel.IsVisible = false; Dispatcher.UIThread.RunJobs();
                    Assert.False(picker.IsDropDownOpen);
                    panel.IsVisible = true; Dispatcher.UIThread.RunJobs();
                }
            }
            finally { window.Close(); }
            return Task.FromResult(true);
        }, CancellationToken.None);
    }
}

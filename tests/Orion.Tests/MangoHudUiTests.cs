using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Orion.Desktop.I18n;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Views;
using Orion.Domain;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class MangoHudUiTests
{
    [Fact]
    public async Task InstanceHudControlsRenderAndPersistTheirBindings()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(() =>
        {
            var text = new Localizer(); text.SetLanguage("pt-BR");
            var model = new LaunchOptionsViewModel(GameInstance.Create("MangoHud", "26.30", "Release"), text, hasMangoHudSystemConfiguration: () => true)
            { SelectedTab = 7 };
            var fields = new LaunchOptionsFields { DataContext = model };
            var window = new Window { Width = 960, Height = 800, Content = fields };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var enabled = fields.FindControl<CheckBox>("MangoHudEnabledToggle")!;
                var fps = fields.FindControl<CheckBox>("MangoHudFpsToggle")!;
                var position = fields.FindControl<ComboBox>("MangoHudPositionPicker")!;
                Assert.True(enabled.IsEffectivelyVisible);
                Assert.True(fps.IsChecked);
                var system = fields.FindControl<CheckBox>("MangoHudSystemConfigToggle")!;
                Assert.True(system.IsChecked);
                Assert.False(fps.IsEffectivelyEnabled);
                Assert.False(position.IsEffectivelyEnabled);
                system.IsChecked = false;
                Dispatcher.UIThread.RunJobs();
                Assert.True(fps.IsEffectivelyEnabled);
                Assert.True(position.IsEffectivelyEnabled);
                fps.IsChecked = false;
                position.SelectedIndex = 3;
                Dispatcher.UIThread.RunJobs();
                Assert.False(model.Build().MangoHud!.Fps);
                Assert.Equal(MangoHudPosition.BottomRight, model.Build().MangoHud!.Position);
                system.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Assert.False(fps.IsEffectivelyEnabled);
                Assert.False(position.IsEffectivelyEnabled);
                Assert.False(model.Build().MangoHud!.Fps);
                Assert.Equal(MangoHudPosition.BottomRight, model.Build().MangoHud!.Position);
                system.IsChecked = false;
                Dispatcher.UIThread.RunJobs();
                position.IsDropDownOpen = true;
                Dispatcher.UIThread.RunJobs();
                Assert.True(position.IsDropDownOpen);
                position.IsDropDownOpen = false;
                enabled.IsChecked = false;
                Dispatcher.UIThread.RunJobs();
                Assert.False(model.Build().MangoHud!.Enabled);
                Assert.False(fps.IsEffectivelyEnabled);
                enabled.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Assert.True(fps.IsEffectivelyEnabled);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
                {
                    Directory.CreateDirectory(output);
                    frame.Save(Path.Combine(output, "instance-mangohud.png"));
                }
            }
            finally { window.Close(); }
            return Task.FromResult(true);
        }, CancellationToken.None);
    }
}

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orion.Desktop.Composition;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Views;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class AboutUiTests
{
    [Theory]
    [InlineData("dark", "orion", "pt-BR", 1180)]
    [InlineData("light", "dune", "en-US", 900)]
    public async Task MaintainersHaveOfflineAvatarsAccessibleLinksAndResponsiveCards(
        string mode, string visual, string language, int width)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            await using var services = new LauncherServices(directory.Paths,
                new(language, ThemeMode: mode, VisualTheme: visual), ["orion"]);
            var window = new MainWindow { Width = width, Height = 820 };
            var model = new MainViewModel(services, window);
            window.DataContext = model;
            window.Show();
            model.Page = "about";
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var about = window.GetVisualDescendants().OfType<AboutPanel>().Single();
            var cards = about.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("maintainer")).ToArray();
            Assert.Equal(2, cards.Length);
            Assert.Equal(new[] { "yPerfectBR", "raonygamer" }, cards.Select(card => card.CommandParameter));
            foreach (var card in cards)
            {
                Assert.True(card.IsEffectivelyVisible);
                Assert.True(card.Focusable);
                Assert.Same(model.OpenMaintainerCommand, card.Command);
                Assert.Equal($"{card.CommandParameter} · GitHub", AutomationProperties.GetName(card));
                Assert.Equal($"https://github.com/{card.CommandParameter}", ToolTip.GetTip(card));
                Assert.True(card.Command!.CanExecute(card.CommandParameter));
                var image = card.GetVisualDescendants().OfType<Image>().Single();
                var avatar = Assert.IsType<Bitmap>(image.Source);
                Assert.Equal(128, avatar.PixelSize.Width);
                Assert.Equal(128, avatar.PixelSize.Height);
                Assert.Equal(52, image.Bounds.Width);
                Assert.True(card.Bounds.Right <= Assert.IsType<WrapPanel>(card.Parent).Bounds.Width);
            }
            Assert.NotSame(cards[0].GetVisualDescendants().OfType<Image>().Single().Source,
                cards[1].GetVisualDescendants().OfType<Image>().Single().Source);
            // Cards wrap on a narrow workspace instead of clipping their labels.
            about.Width = 320;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.True(cards[1].Bounds.Y > cards[0].Bounds.Y);
            about.Width = double.NaN;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.Contains(about.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == model.Text["GitHubProfile"]);
            using var screenshot = window.CaptureRenderedFrame();
            Assert.NotNull(screenshot);
            if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
            {
                Directory.CreateDirectory(output);
                screenshot.Save(Path.Combine(output, $"about-maintainers-{mode}.png"));
            }
            await model.StopAllAsync();
            window.Close();
            return true;
        }, CancellationToken.None);
    }
}

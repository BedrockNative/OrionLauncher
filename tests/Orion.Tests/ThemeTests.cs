using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Automation.Peers;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orion.Desktop.Composition;
using Orion.Desktop.Theming;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Views;
using Orion.Domain;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class ThemeTests
{
    [Fact]
    public async Task OldSettingsKeepDefaultsAndThemeChoicesRoundTrip()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.Paths.Config);
        await File.WriteAllTextAsync(Path.Combine(directory.Paths.Config, "settings.json"), "{\"language\":\"pt-BR\",\"keepInBackground\":false}");
        var store = new SettingsStore(directory.Paths);
        var old = await store.LoadAsync();
        Assert.Equal("dark", old.ThemeMode); Assert.Equal("theme", old.ColorPalette); Assert.Equal("orion", old.VisualTheme);
        var settings = old with { ThemeMode = "system", ColorPalette = "lavender", VisualTheme = "dune" };
        await store.SaveAsync(settings);
        Assert.Equal(settings, await store.LoadAsync());
    }

    [Fact]
    public void PalettesKeepReadableTextAndConsistentSemanticKeys()
    {
        foreach (var palette in ThemeManager.Palettes)
        foreach (var light in new[] { false, true })
        foreach (var theme in VisualThemes.All)
        {
            var resources = ThemeManager.CreateResources(palette, light, theme);
            Color Get(string key) => ((SolidColorBrush)resources["Orion" + key + "Brush"]!).Color;
            foreach (var (foreground, background) in new[]
                { ("Text", "Panel"), ("Text", "Window"), ("Muted", "Sidebar"), ("Muted", "Panel"), ("Muted", "Inset"), ("Accent", "Selection"),
                  ("Accent", "Status"), ("OnAccent", "Accent"), ("ErrorText", "ErrorSurface"),
                  ("WarningText", "WarningSurface"), ("ConsoleText", "Console") })
                Assert.True(Contrast(Get(foreground), Get(background)) >= 4.5,
                    $"{theme.Id}/{palette.Id}/{light}: {foreground} on {background}");
            Assert.Equal(ThemeManager.CreateResources(ThemeManager.Palettes[0], light).Keys.Order(), resources.Keys.Order());
        }
    }

    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Linear(byte component) { var n = component / 255d; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        return (Math.Max(L(a), L(b)) + .05) / (Math.Min(L(a), L(b)) + .05);
    }

    [Fact]
    public async Task ChangesUpdateOpenWindowsAndPersistAutomatically()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            await using var services = new LauncherServices(directory.Paths, new("pt-BR"), ["orion"]);
            var instance = GameInstance.Create("Theme preview", "26.52.03", "Release");
            await new InstanceRepository(directory.Paths).SaveAsync(instance);
            var window = new MainWindow(); var model = new MainViewModel(services, window); window.DataContext = model;
            await model.InitializeAsync(); window.Show(); model.Page = "settings";
            Dispatcher.UIThread.RunJobs();
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "LauncherSettingsTabs");
            tabs.SelectedIndex = 1;
            var logModel = new InstanceLogViewModel(model.Text, true);
            var logWindow = new InstanceLogWindow(logModel); logWindow.Show();
            var app = Avalonia.Application.Current!;
            void Render(string name, Window target)
            {
                Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var bitmap = target.CaptureRenderedFrame(); Assert.NotNull(bitmap);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
                { Directory.CreateDirectory(output); bitmap.Save(Path.Combine(output, name + ".png")); }
            }
            for (var mode = 0; mode < 2; mode++)
            for (var palette = 0; palette < ThemeManager.Palettes.Count; palette++)
            {
                model.Appearance.ModeIndex = mode; model.Appearance.PaletteIndex = palette + 1;
                Render($"orion-theme-{mode}-{palette}", window);
                var variant = mode == 0 ? ThemeVariant.Dark : ThemeVariant.Light;
                Assert.Equal(variant, window.ActualThemeVariant);
                Assert.Equal(variant, logWindow.ActualThemeVariant);
                var expected = ThemeManager.CreateResources(ThemeManager.Palettes[palette], mode == 1);
                Assert.Equal(((SolidColorBrush)expected["OrionWindowBrush"]!).Color, ((ISolidColorBrush)window.Background!).Color);
                Assert.Equal(((ISolidColorBrush)window.Background!).Color, ((ISolidColorBrush)logWindow.Background!).Color);
                Assert.Equal(Color.Parse(mode == 0 ? ThemeManager.Palettes[palette].DarkAccent : ThemeManager.Palettes[palette].LightAccent),
                    app.Styles.OfType<FluentTheme>().Single().Palettes[variant].Accent);
                Assert.Equal(mode, window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ThemeModePicker").SelectedIndex);
                Assert.Equal(palette + 1, window.GetVisualDescendants().OfType<ListBox>().Single(c => c.Name == "ThemePalettePicker").SelectedIndex);
            }
            await model.SaveSettingsCommand.ExecuteAsync(null); // Flush the same automatic queue.
            Assert.Equal("light", (await services.SettingsStore.LoadAsync()).ThemeMode);
            Assert.Equal("amber", services.Settings.ColorPalette);
            window.Width = 900; window.Height = 640;
            Render("orion-theme-small-window", window);
            window.Width = 1180; window.Height = 780;
            model.Appearance.ModeIndex = 1; model.Appearance.PaletteIndex = 2;
            Render("orion-theme-light-logs", logWindow);
            model.Page = "library"; model.EditLaunchOptions(model.Instances.Single());
            Render("orion-theme-light-instance", window);
            model.CloseLaunchOptionsCommand.Execute(null);
            model.Appearance.ModeIndex = 2;
            model.LanguageIndex = 0;
            Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
            // Theme dictionaries resolve for the OS-selected variant too.
            Assert.Equal(window.ActualThemeVariant, logWindow.ActualThemeVariant);
            await model.SaveSettingsCommand.ExecuteAsync(null);
            Assert.False(model.HasError);
            var saved = await services.SettingsStore.LoadAsync();
            Assert.Equal("system", saved.ThemeMode); Assert.Equal("blue", saved.ColorPalette);
            Assert.Equal("Follow system", model.Appearance.SelectedMode.Label);
            Assert.Equal("Blue", model.Appearance.SelectedPalette.Label);
            var restored = new ThemeSettingsViewModel(saved, model.Text);
            Assert.Equal(2, restored.ModeIndex); Assert.Equal(2, restored.PaletteIndex);
            var fallback = new ThemeSettingsViewModel(saved with { ThemeMode = "unknown", ColorPalette = "unknown" }, model.Text);
            Assert.Equal(0, fallback.ModeIndex); Assert.Equal(0, fallback.PaletteIndex);
            model.Appearance.ResetCommand.Execute(null);
            model.EditLaunchOptions(model.Instances.Single());
            Render("orion-theme-soft-mint-instance", window);
            await model.SaveSettingsCommand.ExecuteAsync(null);
            Assert.Equal("dark", (await services.SettingsStore.LoadAsync()).ThemeMode);
            await model.StopAllAsync();
            logWindow.Close(); window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task VisualThemesChangeAllSurfacesAndPreserveAccentsWithAccessibleCompactSettings()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            await using var services = new LauncherServices(directory.Paths, new("pt-BR"), ["orion"]);
            var window = new MainWindow { Width = 1920, Height = 1080 };
            var model = new MainViewModel(services, window); window.DataContext = model;
            await model.InitializeAsync(); window.Show(); model.Page = "settings";
            void Render(string name)
            {
                Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
                { Directory.CreateDirectory(output); bitmap.Save(Path.Combine(output, name + ".png")); }
            }
            Render("orion-general-compact-large");
            var card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "GeneralSettingsCard");
            Assert.InRange(card.Bounds.Height, 150, 350);
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "LauncherSettingsTabs");
            tabs.SelectedIndex = 1;
            window.Width = 1280; window.Height = 1020;
            var app = Avalonia.Application.Current!;
            var backgrounds = new HashSet<Color>();
            foreach (var mode in new[] { 0, 1 })
            for (var visual = 0; visual < VisualThemes.All.Count; visual++)
            {
                model.Appearance.ModeIndex = mode; model.Appearance.VisualIndex = visual;
                Render($"orion-visual-{mode}-{VisualThemes.All[visual].Id}");
                var surfaces = mode == 0 ? VisualThemes.All[visual].Dark : VisualThemes.All[visual].Light;
                Assert.Equal(Color.Parse(surfaces.Window), ((ISolidColorBrush)window.Background!).Color);
                backgrounds.Add(((ISolidColorBrush)window.Background!).Color);
                var variant = mode == 0 ? ThemeVariant.Dark : ThemeVariant.Light;
                Assert.Equal(Color.Parse(surfaces.Panel), app.Styles.OfType<FluentTheme>().Single().Palettes[variant].RegionColor);
                var recommended = ThemeManager.ResolvePalette("theme", VisualThemes.All[visual]);
                Assert.Equal(Color.Parse(mode == 0 ? recommended.DarkAccent : recommended.LightAccent), app.Styles.OfType<FluentTheme>().Single().Palettes[variant].Accent);
            }
            Assert.Equal(8, backgrounds.Count);
            var selectors = window.GetVisualDescendants().OfType<SelectingItemsControl>();
            foreach (var name in new[] { "ThemeModePicker", "VisualThemePicker", "ThemePalettePicker" })
            {
                var selector = selectors.Single(s => s.Name == name);
                Assert.False(string.IsNullOrWhiteSpace(ControlAutomationPeer.CreatePeerForElement(selector)!.GetName()));
                if (selector is not ListBox list) continue;
                for (var i = 0; i < list.ItemCount; i++)
                    Assert.Equal(((ThemeOptionViewModel)list.Items[i]!).Label, ControlAutomationPeer.CreatePeerForElement(list.ContainerFromIndex(i)!)!.GetName());
            }
            var visuals = window.GetVisualDescendants().OfType<ListBox>().Single(c => c.Name == "VisualThemePicker");
            model.Appearance.VisualIndex = 0;
            visuals.ContainerFromIndex(0)!.Focus(NavigationMethod.Tab);
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            Assert.Equal(1, model.Appearance.VisualIndex);
            model.Appearance.PaletteIndex = 2; // Explicit blue, independent of visual theme.
            model.Appearance.VisualIndex = 3;
            Assert.Equal("blue", model.Appearance.SelectedPalette.Id);
            await model.SaveSettingsCommand.ExecuteAsync(null);
            Assert.Equal("dune", (await services.SettingsStore.LoadAsync()).VisualTheme);
            model.Appearance.VisualIndex = 2;
            await model.SaveSettingsCommand.ExecuteAsync(null);
            Assert.False(model.HasError); Assert.False(model.Appearance.IsDirty);
            Assert.Equal("grove", (await services.SettingsStore.LoadAsync()).VisualTheme);
            model.Appearance.ResetCommand.Execute(null);
            await model.SaveSettingsCommand.ExecuteAsync(null);
            Assert.Equal("orion", (await services.SettingsStore.LoadAsync()).VisualTheme);
            Assert.False(model.Appearance.IsDirty);
            window.Width = 900; window.Height = 640;
            Render("orion-visual-small");
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "ThemeSettingsScroll");
            scroll.Offset = new Avalonia.Vector(0, scroll.Extent.Height);
            Render("orion-visual-small-bottom");
            Assert.True(scroll.Offset.Y > 0);
            Assert.InRange(scroll.Extent.Width, 0, scroll.Viewport.Width + 1);
            await model.StopAllAsync(); window.Close(); return true;
        }, CancellationToken.None);
    }
}

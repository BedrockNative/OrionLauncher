using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orion.Desktop.Composition;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Views;
using Orion.Domain;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class ContentLibraryUiTests
{
    [Fact]
    public async Task LibraryProjectRowsShowCachedIconsMetadataAndResponsiveSelection()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var dir = new TestDirectory();
            await using var services = new LauncherServices(dir.Paths, new("pt-BR"), ["orion"]);
            var window = new MainWindow { Width = 1280, Height = 940 };
            var model = new MainViewModel(services, window); window.DataContext = model;
            await model.InitializeAsync(); window.Show(); model.Page = "content";
            for (var i = 0; model.ContentLibrary.Busy && i < 100; i++) await Task.Delay(10);
            var archive = ContentTests.Zip(dir, ".mcaddon",
                ("bp/manifest.json", ContentTests.Manifest("Bosques & caminhos BP", "data")),
                ("rp/manifest.json", ContentTests.Manifest("Bosques & caminhos RP", "resources")),
                ("bp/pack_icon.png", "broken icon"));
            // A damaged behavior-pack icon must fall back to the resource-pack icon.
            using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Update))
            using (var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(64, 64)))
            {
                surface.Canvas.Clear(SkiaSharp.SKColors.ForestGreen);
                using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Gold };
                surface.Canvas.DrawCircle(32, 32, 18, paint);
                using var image = surface.Snapshot(); using var data = image.Encode();
                using var stream = zip.CreateEntry("rp/pack_icon.png").Open(); data.SaveTo(stream);
            }
            var download = Path.Combine(dir.Root, "download.mcaddon"); File.Move(archive, download);
            await model.ContentLibrary.ImportAsync(download); Assert.Null(model.ContentLibrary.Error);
            var row = Assert.Single(model.ContentLibrary.Items);
            Assert.Equal("Bosques & caminhos", row.Name); Assert.Equal("×2", row.PackCount);
            Assert.Equal(model.Text["ContentAddons"], row.KindLabel); Assert.NotNull(row.Icon);
            var icon = row.Icon;
            model.ContentLibrary.Search = "nothing matches"; Assert.Empty(model.ContentLibrary.Items);
            model.ContentLibrary.Search = "bosques";
            Assert.Same(row, Assert.Single(model.ContentLibrary.Items)); Assert.Same(icon, row.Icon);
            model.ContentLibrary.Search = "";
            await model.ContentLibrary.ImportAsync(ContentTests.Zip(dir, ".mcpack", ("manifest.json", ContentTests.Manifest("Pedras e jardins — uma coleção de texturas com um nome muito comprido", "resources"))));
            Assert.Null(model.ContentLibrary.Selected!.Icon);
            model.ContentLibrary.Selected = model.ContentLibrary.Items.First(i => i.Name == "Bosques & caminhos");
            foreach (var mode in new[] { "dark", "light" })
            foreach (var width in new[] { 1280, 900 })
            {
                model.Appearance.ModeIndex = model.Appearance.Modes.ToList().FindIndex(m => m.Id == mode);
                window.Width = width; Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var list = window.GetVisualDescendants().OfType<ListBox>().Single(b => b.Classes.Contains("libraryProjects"));
                Assert.InRange(list.Bounds.Width, 220, 360);
                Assert.Contains(list.GetVisualDescendants().OfType<Image>(), i => i.Source is not null);
                foreach (var item in list.GetVisualDescendants().OfType<ListBoxItem>()) Assert.True(item.Bounds.Width <= list.Bounds.Width);
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
                { Directory.CreateDirectory(output); frame.Save(Path.Combine(output, $"library-projects-{mode}-{width}.png")); }
            }
            await model.StopAllAsync(); window.Close();
            Assert.Empty(model.ContentLibrary.Items); return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LibraryThumbnailsBoundTallImagesWithoutUpscalingSmallIcons()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(() =>
        {
            using var dir = new TestDirectory();
            foreach (var size in new[] { (Width: 16, Height: 1024), (Width: 1024, Height: 16), (Width: 32, Height: 32) })
            {
                var path = Path.Combine(dir.Root, "icon.png");
                using (var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(size.Width, size.Height)))
                {
                    surface.Canvas.Clear(SkiaSharp.SKColors.ForestGreen);
                    using var image = surface.Snapshot(); using var data = image.Encode();
                    using var stream = File.Create(path); data.SaveTo(stream);
                }
                using var row = new LibraryPackRow(new("id", "Pack", ContentKind.Texture, "1.0.0", null, null) { IconPath = path });
                Assert.NotNull(row.Icon);
                Assert.InRange(row.Icon.PixelSize.Width, 1, Math.Min(size.Width, 192));
                Assert.InRange(row.Icon.PixelSize.Height, 1, Math.Min(size.Height, 192));
            }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DedicatedLibraryImportsDistributesAndRendersAlongsideModernLogToolbar()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var dir = new TestDirectory();
            await using var services = new LauncherServices(dir.Paths, new("pt-BR"), ["orion"]);
            var first = GameInstance.Create("Mundo criativo", "26.52.03", "Release"); var second = GameInstance.Create("Sobrevivência", "26.52.03", "Release");
            foreach (var instance in new[] { first, second }) { ContentTests.Setup(dir, instance.Id); await new InstanceRepository(dir.Paths).SaveAsync(instance); }
            var window = new MainWindow { Width = 1280, Height = 940 }; var model = new MainViewModel(services, window); window.DataContext = model;
            await model.InitializeAsync(); window.Show(); model.Page = "content";
            await model.ContentLibrary.RefreshAsync();
            // Navigating calls the method directly; wait for that owned operation if it is still finishing.
            for (var i = 0; model.ContentLibrary.Busy && i < 100; i++) await Task.Delay(10);
            var manifest = System.Text.Json.JsonSerializer.Serialize(new { format_version = 2,
                header = new { name = "Bosques & caminhos", description = "Blocos e detalhes para construir trilhas, jardins e vilas.", uuid = Guid.NewGuid(), version = new[] { 2, 1, 0 }, min_engine_version = new[] { 1, 21, 0 } },
                modules = new[] { new { type = "resources" } } });
            var file = ContentTests.Zip(dir, ".mcpack", ("manifest.json", manifest));
            await model.ContentLibrary.ImportAsync(file); Assert.Null(model.ContentLibrary.Error);
            Assert.Equal("Bosques & caminhos", model.ContentLibrary.Selected!.Name);
            model.ContentLibrary.Target = model.ContentLibrary.Targets.Single(t => t.Instance.Id == first.Id);
            await model.ContentLibrary.DistributeCommand.ExecuteAsync(null);
            Assert.Null(model.ContentLibrary.Error);
            Assert.Contains("1/1", model.ContentLibrary.Targets.Single(t => t.Instance.Id == first.Id).ContentStatus);
            Assert.Contains("0/1", model.ContentLibrary.Targets.Single(t => t.Instance.Id == second.Id).ContentStatus);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.CommandParameter, "content") && b.IsEffectivelyVisible);
            void Capture(string name)
            {
                Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output) { Directory.CreateDirectory(output); bitmap.Save(Path.Combine(output, name + ".png")); }
            }
            Capture("content-library"); window.Width = 900; window.Height = 700; Capture("content-library-small");
            model.Page = "library";
            File.WriteAllText(dir.Paths.InstanceLog(first.Id), "[Orion] Preparando instância\n[Xodus] Perfil pronto\n[WineGDK] Jogo iniciado\n");
            await model.Log.OpenAsync(first.Name, dir.Paths.InstanceLog(first.Id)); Capture("log-toolbar");
            var follow = window.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Classes.Contains("logAction"));
            Assert.Equal(32, follow.Bounds.Height); Assert.True(follow.IsChecked);
            follow.IsChecked = false; Assert.False(model.Log.Follow);
            var copy = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == model.Text["CopyLog"]);
            Assert.Equal(32, copy.Bounds.Height);
            await model.StopAllAsync(); window.Close(); return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SettingsPersistWithoutSaveAndShutdownFlushesTheLastChoice()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var dir = new TestDirectory(); await using var services = new LauncherServices(dir.Paths, new(), ["orion"]);
            var window = new MainWindow(); var model = new MainViewModel(services, window); window.DataContext = model;
            window.Show(); model.Page = "settings"; Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && Equals(b.Content, model.Text["Save"]));
            Assert.True(model.CheckRuntimeUpdatesOnStartup);
            model.CheckRuntimeUpdatesOnStartup = false;
            model.LanguageIndex = 1; model.KeepInBackground = false;
            model.Appearance.VisualIndex = 3; model.Appearance.Advanced.Dark.AccentIndex = 1; model.Appearance.Advanced.Dark.CustomAccent = "#123456";
            for (var i = 0; i < 100 && services.Settings.Appearance.Dark.CustomAccent != "#123456"; i++) await Task.Delay(10);
            var saved = await services.SettingsStore.LoadAsync(); Assert.Equal("pt-BR", saved.Language); Assert.False(saved.KeepInBackground);
            Assert.False(saved.CheckRuntimeUpdatesOnStartup);
            Assert.Equal("dune", saved.VisualTheme); Assert.Equal("#123456", saved.Appearance.Dark.CustomAccent);
            model.Appearance.Advanced.Dark.CustomAccent = "#bad";
            await Task.Delay(350); Assert.Equal("#123456", (await services.SettingsStore.LoadAsync()).Appearance.Dark.CustomAccent);
            model.Appearance.Advanced.Dark.CustomAccent = "#ABCDEF"; model.Appearance.Advanced.TopNavigation = true;
            await model.StopAllAsync(); window.Close();
            saved = await services.SettingsStore.LoadAsync(); Assert.Equal("#ABCDEF", saved.Appearance.Dark.CustomAccent); Assert.True(saved.Appearance.TopNavigation);
            return true;
        }, CancellationToken.None);
    }
    [Fact]
    public async Task SettingsQueueSerializesWritesCoalescesRapidEditsAndAllowsRetry()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            List<string> persisted = []; var active = 0; var fail = true; var errors = 0;
            var queue = new SettingsAutoSave(async settings =>
            {
                Assert.Equal(1, ++active);
                try { await Task.Delay(10); if (fail) throw new IOException("test"); persisted.Add(settings.VisualTheme); }
                finally { active--; }
            }, _ => errors++);
            queue.Queue(new(VisualTheme: "grove")); queue.Queue(new(VisualTheme: "dune")); await queue.FlushAsync(); Assert.Equal(1, errors);
            fail = false; queue.Queue(new(VisualTheme: "dune")); await queue.FlushAsync(); Assert.Equal(new[] { "dune" }, persisted);
            queue.Queue(new(VisualTheme: "orion")); queue.Queue(new(VisualTheme: "graphite")); await queue.FlushAsync();
            Assert.Equal(new[] { "dune", "graphite" }, persisted); return true;
        }, CancellationToken.None);
    }
}

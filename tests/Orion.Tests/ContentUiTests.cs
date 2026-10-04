using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orion.Desktop.I18n;
using Orion.Desktop.Theming;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Views;
using Orion.Infrastructure.Content;
using Orion.Domain;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.CurseForge;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class ContentUiTests
{
    [Fact]
    public async Task HomeShowsCoverCardsBeforeSearchingAndDetailsAdaptToSmallWindows()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var dir = new TestDirectory(); var id = Guid.NewGuid(); ContentTests.Setup(dir, id);
            using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(320, 180));
            surface.Canvas.Clear(SkiaSharp.SKColors.Teal);
            using var image = surface.Snapshot(); using var bytes = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            using var pictures = new HttpClient(new ReleaseTests.Handler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) };
                response.Content.Headers.ContentType = new("image/png"); return response;
            }));
            using var covers = new Orion.Desktop.Content.CoverStore(pictures, Path.Combine(dir.Root, "covers"));
            var projects = Enumerable.Range(1, 6).Select(i => new CfProject(i, 78022, "Bedrock adventure " + i,
                "Discover new places and build your next adventure.", [new("Creator")], new("https://www.curseforge.com/"), true, i * 1000)
                { Logo = new("https://media.forgecdn.net/fixture.png", null) }).ToArray();
            using var api = new CurseForgeClient(new ReleaseTests.Handler(_ => new(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new CfPage<CfProject>(projects, new(0, 6, 6)), new JsonSerializerOptions(JsonSerializerDefaults.Web))) }), new Handler(), () => "fixture");
            var activity = new Orion.Infrastructure.Games.InstanceActivity(); var local = new InstanceContentService(dir.Paths, activity);
            var text = new Localizer(); text.SetLanguage("pt-BR");
            var model = new CurseForgeViewModel(api, new(new ContentLibraryService(dir.Paths, activity, local), local,
                () => Task.FromResult<IReadOnlyList<GameInstance>>([]), text), text, covers);
            var panel = new CurseForgePanel { DataContext = model, Margin = new Thickness(24) };
            var window = new Window { Width = 1100, Height = 850, Content = panel }; window.Show(); await model.OpenAsync();
            for (var i = 0; i < 200 && panel.GetVisualDescendants().OfType<Image>().Count(x => x.Source is not null) < 6; i++) await Task.Delay(10);
            Assert.Equal(6, panel.GetVisualDescendants().OfType<Image>().Count(x => x.Source is not null));
            Assert.False(panel.FindControl<Border>("ProjectDetails")!.IsEffectivelyVisible);
            Assert.Equal(2, Grid.GetColumnSpan(panel.FindControl<Border>("ProjectResults")!));
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using (var frame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(frame);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output) { Directory.CreateDirectory(output); frame.Save(Path.Combine(output, "curseforge-home-covers.png")); }
            }
            foreach (var width in new[] { 700, 1600 })
            {
                window.Width = width; ThemeManager.Apply(width == 700 ? "light" : "dark", "theme", "orion");
                Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var list = panel.FindControl<ListBox>("ProjectList")!;
                Assert.InRange(panel.ProjectCardWidth, 120, 340);
                foreach (var item in list.GetVisualDescendants().OfType<ListBoxItem>())
                {
                    Assert.Equal(new CornerRadius(16), item.CornerRadius);
                    var point = item.TranslatePoint(default, list); Assert.NotNull(point);
                    Assert.InRange(point.Value.X + item.Bounds.Width, 0, list.Bounds.Width + 1);
                }
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output) frame.Save(Path.Combine(output, $"curseforge-home-{width}.png"));
            }
            window.Width = 1100; Dispatcher.UIThread.RunJobs();
            model.Project = model.Projects[0]; Dispatcher.UIThread.RunJobs();
            Assert.True(panel.FindControl<Border>("ProjectDetails")!.IsEffectivelyVisible);
            Assert.Equal(1, Grid.GetColumnSpan(panel.FindControl<Border>("ProjectResults")!));
            window.Width = 700; Dispatcher.UIThread.RunJobs();
            Assert.False(panel.FindControl<Border>("ProjectResults")!.IsEffectivelyVisible);
            Assert.Equal(2, Grid.GetColumnSpan(panel.FindControl<Border>("ProjectDetails")!));
            model.CloseProjectCommand.Execute(null); Dispatcher.UIThread.RunJobs();
            Assert.True(panel.FindControl<Border>("ProjectResults")!.IsEffectivelyVisible);
            panel.IsVisible = false; Assert.Equal(0, covers.LoadedCount);
            await model.StopAsync(); window.Close(); return true;
        }, CancellationToken.None);
    }
    [Fact]
    public async Task MainNavigationExposesDiscoveryAndTypesBeforeAnyRequest()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            await using var services = new Orion.Desktop.Composition.LauncherServices(directory.Paths, new("pt-BR"), ["orion"]);
            var window = new MainWindow { Width = 1180, Height = 780 };
            var model = new MainViewModel(services, window); window.DataContext = model; window.Show();
            model.Page = "curseforge";
            while (model.CurseForge.IsBusy) await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
            var panel = window.GetVisualDescendants().OfType<CurseForgePanel>().Single();
            Assert.True(panel.IsEffectivelyVisible);
            var picker = panel.FindControl<ComboBox>("CfCategoryPicker")!;
            Assert.Equal(3, picker.ItemCount); Assert.Equal("Addons", ((CfCategory)picker.SelectedItem!).Name);
            picker.IsDropDownOpen = true; Dispatcher.UIThread.RunJobs();
            Assert.True(picker.IsDropDownOpen);
            Assert.NotNull(picker.ContainerFromIndex(2));
            picker.SelectedIndex = 2; picker.IsDropDownOpen = false;
            Assert.Equal(CurseForgeClient.TexturesClassId, model.CurseForge.Category!.Id);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using (var frame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(frame);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output) { Directory.CreateDirectory(output); frame.Save(Path.Combine(output, "curseforge-main.png")); }
            }
            model.Appearance.Advanced.TopNavigation = true; window.Width = 900; window.Height = 640;
            Dispatcher.UIThread.RunJobs();
            foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.CommandParameter is string))
            {
                var point = button.TranslatePoint(default, window); Assert.NotNull(point);
                Assert.InRange(point.Value.X + button.Bounds.Width, 0, 901);
            }
            await model.StopAllAsync(); window.Close(); return true;
        }, CancellationToken.None);
    }
    internal static CurseForgeViewModel Browser(TestDirectory directory, Guid id, CurseForgeClient client, Localizer text)
    {
        var activity = new InstanceActivity();
        var local = new InstanceContentService(directory.Paths, activity);
        return new(client, new(new ContentLibraryService(directory.Paths, activity, local), local,
            () => Task.FromResult<IReadOnlyList<GameInstance>>([GameInstance.Create("Fixture", "26.50", "Release") with { Id = id }]), text), text);
    }
    private sealed class ManualHandler(CfFile file) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Assert.Equal("api.curseforge.com", r.RequestUri!.Host);
            object result = r.RequestUri.AbsolutePath.EndsWith("/files/99") ? new { data = file }
                : new { data = new CfProject(42, 78022, "Browser download", "Fixture", [], new("https://www.curseforge.com/minecraft-bedrock/addons/example"), false, 100) };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web))) });
        }
    }
    [Fact]
    public async Task BrowserHandoffRendersImportsVerifiedCopyAndCancelsWithoutDeletingOriginal()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory(); var id = Guid.NewGuid(); ContentTests.Setup(directory, id);
            var content = new InstanceContentService(directory.Paths, new());
            var pack = ContentTests.Zip(directory, ".mcpack", ("manifest.json", ContentTests.Manifest("Manual download", "resources")));
            var file = BrowserDownloadTests.Metadata(await File.ReadAllBytesAsync(pack));
            using var client = new CurseForgeClient(new ManualHandler(file), new ManualHandler(file), () => "fixture");
            var text = new Localizer(); text.SetLanguage("pt-BR");
            var browserCalls = 0;
            var model = Browser(directory, id, client, text);
            model.DownloadFolder = Directory.CreateDirectory(Path.Combine(directory.Root, "browser-downloads")).FullName;
            model.OpenBrowserAsync = page =>
                {
                    Assert.Equal("https://www.curseforge.com/minecraft-bedrock/addons/example/files/99", page.AbsoluteUri);
                    browserCalls++; return Task.FromResult(false); // browser failure must not stop folder detection
                };
            await model.RefreshAsync();
            model.Destinations.ModeIndex = 1;
            model.Project = new(42, 78022, "Fixture", "", [], new("https://www.curseforge.com/minecraft-bedrock/addons/example"), false, 100);
            model.SelectedFile = file;
            var window = new Window { Width = 1040, Height = 780, Content = new CurseForgePanel { DataContext = model } }; window.Show();
            var installation = model.InstallFileCommand.ExecuteAsync(null);
            Assert.True(model.IsManualDownload); Assert.True(model.IsBusy); Assert.NotNull(model.ManualBrowserError); Assert.Equal(1, browserCalls);
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using (var bitmap = window.CaptureRenderedFrame())
            {
                Assert.NotNull(bitmap);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
                { Directory.CreateDirectory(output); bitmap.Save(Path.Combine(output, "content-browser-download.png")); }
            }
            var choose = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, text["CfManualChooseFolder"]));
            Assert.True(choose.IsEffectivelyEnabled);
            var downloaded = Path.Combine(model.DownloadFolder, "example (1).mcpack"); File.Copy(pack, downloaded);
            await installation.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.False(model.IsManualDownload); Assert.False(model.IsBusy); Assert.Null(model.Error);
            Assert.Single((await content.ListAsync(id)).Entries); Assert.True(File.Exists(downloaded));
            model.DownloadFolder = Path.Combine(directory.Root, "not-created");
            var cancelled = model.InstallFileCommand.ExecuteAsync(null); Assert.True(model.IsManualDownload);
            await model.StopAsync(); await cancelled;
            Assert.False(model.IsManualDownload); Assert.False(model.IsBusy); Assert.True(File.Exists(downloaded));
            Assert.Single((await content.ListAsync(id)).Entries); window.Close(); return true;
        }, CancellationToken.None);
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            object data = r.RequestUri!.AbsolutePath.Contains("categories") ? new { data = new[] {
                new CfCategory(4984, "Addons", true), new(6913, "Maps", true), new(6929, "Textures", true),
                new(6925, "Skins", true), new(6940, "Scripts", true) } }
                : r.RequestUri.AbsolutePath.EndsWith("/files") ? new { data = new[] { new CfFile(99, 42, 78022, "Bedrock 1.21", "example.mcaddon", 1024, true, null, [], ["1.21"], []) }, pagination = new { index = 0, resultCount = 1, totalCount = 1 } }
                : new { data = new[] { new CfProject(42, 78022, "Example building collection", "A test fixture showcasing the project's description and author without external requests.", [new("Example creator")], new("https://www.curseforge.com/"), true, 1000) }, pagination = new { index = 0, resultCount = 1, totalCount = 1 } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web))) });
        }
    }
    [Fact]
    public async Task ContentAndDiscoveryRenderAndCommandsRespectWindowLifetime()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory(); var id = Guid.NewGuid(); ContentTests.Setup(directory, id);
            var service = new InstanceContentService(directory.Paths, new());
            await service.ImportAsync(id, ContentTests.Zip(directory, ".mcpack", ("manifest.json", ContentTests.Manifest("Cozy landscapes", "resources"))), null);
            using var cf = new CurseForgeClient(new Handler(), new Handler(), () => "fixture");
            var text = new Localizer(); text.SetLanguage("pt-BR");
            var model = new ContentManagementViewModel(service, id, "Sobrevivência com amigos", text);
            await model.RefreshAsync(); model.KindIndex = 2;
            Assert.Single(model.Items); Assert.Single(model.Profiles);
            var window = new ContentWindow { DataContext = model }; window.Show();
            void Render(string name)
            {
                Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
                { Directory.CreateDirectory(output); frame.Save(Path.Combine(output, name + ".png")); }
            }
            ThemeManager.Apply("dark", "theme", "orion"); Render("content-installed-dark");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == text["ContentExperimentalNotice"]);
            Assert.Contains("experimentais", text["ContentExperimentalNotice"]);
            model.Selected = model.Items[0]; model.RequestArchiveCommand.Execute(null); Assert.True(model.ConfirmArchive);
            await model.ArchiveCommand.ExecuteAsync(null); Assert.Empty(model.Items);
            model.Archived = true; model.Selected = Assert.Single(model.Items); await model.RestoreCommand.ExecuteAsync(null);
            model.Archived = false; Assert.Single(model.Items);
            var browser = Browser(directory, id, cf, text); await browser.RefreshAsync();
            await browser.SearchProjectsCommand.ExecuteAsync(null); browser.Project = Assert.Single(browser.Projects);
            await browser.LoadFilesCommand.ExecuteAsync(null); Assert.Single(browser.Files);
            var panel = new CurseForgePanel { DataContext = browser };
            window.Content = panel;
            Render("content-curseforge-dark");
            var categoryPicker = panel.FindControl<ComboBox>("CfCategoryPicker")!;
            Assert.Equal(new[] { 4984, 6913, 6929 }, categoryPicker.Items.Cast<CfCategory>().Select(c => c.Id));
            Assert.Equal("Mapas", browser.Categories.Single(c => c.Id == 6913).Name);
            ThemeManager.Apply("light", "theme", "dune"); window.Width = 800; window.Height = 640; Render("content-curseforge-light-small");
            Assert.All(window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible), b => Assert.True(b.Bounds.Width > 0));
            await model.StopAsync(); await model.RefreshAsync(); Assert.False(model.IsBusy);
            await browser.StopAsync(); window.Close(); ThemeManager.Apply("dark", "theme", "orion"); return true;
        }, CancellationToken.None);
    }
}

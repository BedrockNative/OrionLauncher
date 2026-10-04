using System.Net;
using System.Net.Http.Headers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orion.Desktop;
using Orion.Desktop.Composition;
using Orion.Desktop.Content;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Views;
using Orion.Domain;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class CoverTests
{
    [Fact]
    public async Task ProjectCoversAreAllowlistedSharedCachedAndReleasedWhenPageHides()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var dir = new TestDirectory(); var bytes = PictureBytes(); var requests = 0;
            using var http = new HttpClient(new ReleaseTests.Handler(request =>
            {
                Assert.False(request.Headers.Contains("x-api-key")); Assert.Null(request.Headers.Authorization);
                Assert.Equal("media.forgecdn.net", request.RequestUri!.Host);
                requests++; return Picture(bytes);
            }));
            using var store = new CoverStore(http, Path.Combine(dir.Root, "project-covers"));
            foreach (var url in new[] { "http://media.forgecdn.net/image.png", "https://media.forgecdn.net.evil.test/image.png", "file:///etc/passwd", "https://user@media.forgecdn.net/image.png" })
                Assert.Null(await store.AcquireCurseForgeAsync(url, default));
            Assert.Equal(0, requests);
            const string image = "https://media.forgecdn.net/avatars/fixture.png";
            var project = new Orion.Infrastructure.CurseForge.CfProject(1, 78022, "Fixture", "", [], new(""), true, 10)
                { Logo = new(image, "https://media.forgecdn.net/original.png") };
            Assert.Equal(image, project.CoverUrl);
            Assert.Null((project with { Logo = new("https://untrusted.test/a", null) }).CoverUrl);
            var panel = new StackPanel();
            var window = new Window { Content = panel }; window.Show();
            panel.Children.Add(new ProjectCoverView { Store = store, SourceUrl = project.CoverUrl, Width = 240, Height = 128 });
            panel.Children.Add(new ProjectCoverView { Store = store, SourceUrl = project.CoverUrl, Width = 240, Height = 128 });
            await Eventually(() => panel.GetVisualDescendants().OfType<Image>().Count(i => i.Source is not null) == 2);
            Assert.Equal(1, requests); Assert.Equal(1, store.LoadedCount);
            panel.IsVisible = false; Assert.Equal(0, store.LoadedCount);
            panel.IsVisible = true;
            await Eventually(() => panel.GetVisualDescendants().OfType<Image>().Count(i => i.Source is not null) == 2);
            Assert.Equal(1, requests);
            window.Close(); Assert.Equal(0, store.LoadedCount);
            return true;
        }, CancellationToken.None);
    }

    private static byte[] PictureBytes()
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(640, 360));
        using var output = new MemoryStream();
        bitmap.Save(output); return output.ToArray();
    }
    private static HttpResponseMessage Picture(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return response;
    }
    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task CoversAreOptInSharedReleasedAndCachedForOfflineReuse()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            var bytes = PictureBytes();
            var requests = 0;
            using var http = new HttpClient(new ReleaseTests.Handler(_ => { requests++; return Picture(bytes); }));
            var cache = Path.Combine(directory.Root, "covers");
            using var store = new CoverStore(http, cache);
            Assert.Null(await store.AcquireAsync(null, default));
            Assert.Null(await store.AcquireAsync("../../untrusted", default));
            Assert.Equal(0, requests); Assert.False(Directory.Exists(cache));
            var leases = await Task.WhenAll(store.AcquireAsync("mountains", default), store.AcquireAsync("mountains", default));
            Assert.Equal(1, requests);
            Assert.Same(leases[0]!.Bitmap, leases[1]!.Bitmap);
            Assert.Equal(320, leases[0]!.Bitmap.PixelSize.Width);
            Assert.True(leases[0]!.Bitmap.PixelSize.Height <= 320);
            leases[0]!.Dispose(); Assert.Equal(1, store.LoadedCount);
            leases[1]!.Dispose(); Assert.Equal(0, store.LoadedCount);
            using var offlineHttp = new HttpClient(new ReleaseTests.Handler(_ => throw new HttpRequestException("Offline")));
            using var offline = new CoverStore(offlineHttp, cache);
            using (var reused = await offline.AcquireAsync("mountains", default)) Assert.NotNull(reused);
            Assert.Equal(0, offline.LoadedCount);
            await File.WriteAllTextAsync(Path.Combine(cache, "mountains-320.png"), "corrupt disposable cache");
            using (var recovered = await store.AcquireAsync("mountains", default)) Assert.NotNull(recovered);
            Assert.Equal(2, requests);
            Assert.Empty(Directory.GetFiles(cache, "*.part"));
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task InvalidResponsesAndCancelledLoadsLeaveNoImagesOrPartialFiles()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            var cache = Path.Combine(directory.Root, "covers");
            foreach (var content in new HttpContent[] { new StringContent("<html>error</html>"), new ByteArrayContent(new byte[CoverStore.MaximumDownloadBytes + 1]), new ByteArrayContent([1, 2, 3]) })
            {
                if (content is ByteArrayContent and not StringContent) content.Headers.ContentType = new("image/png");
                using var http = new HttpClient(new ReleaseTests.Handler(_ => new(HttpStatusCode.OK) { Content = content }));
                using var store = new CoverStore(http, cache);
                await Assert.ThrowsAnyAsync<Exception>(() => store.AcquireAsync("jungle", default));
                Assert.Equal(0, store.LoadedCount);
            }
            using var cancelledHttp = new HttpClient(new ReleaseTests.Handler(_ => throw new InvalidOperationException("Must not download")));
            using var cancelled = new CoverStore(cancelledHttp, cache);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.AcquireAsync("jungle", cancellation.Token));
            Assert.False(Directory.Exists(cache));
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CardsReleaseHiddenImagesAndAppearanceCanBeSavedCancelledAndRemoved()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            var cache = Path.Combine(directory.Paths.Cache, "covers");
            Directory.CreateDirectory(cache);
            foreach (var cover in InstanceCovers.All)
            {
                var fixtureName = cover.Id == "lush-caves" ? "caves" : cover.Id;
                var fixture = Environment.GetEnvironmentVariable("ORION_COVER_FIXTURES") is { } fixtures
                    ? Path.Combine(fixtures, $"orion-{fixtureName}.jpg") : null;
                using var input = new MemoryStream(fixture is not null ? await File.ReadAllBytesAsync(fixture) : PictureBytes());
                using var bitmap = Bitmap.DecodeToWidth(input, 320);
                bitmap.Save(Path.Combine(cache, cover.Id + "-320.png"));
            }
            var repository = new InstanceRepository(directory.Paths);
            var first = GameInstance.Create("Mountain retreat", "26.52.03", "Release") with { CoverId = "mountains" };
            var second = GameInstance.Create("Lush caverns", "26.52.03", "Preview") with { CoverId = "lush-caves" };
            await repository.SaveAsync(first); await repository.SaveAsync(second);
            await repository.SaveAsync(GameInstance.Create("Minimal", "26.52.03", "Release"));
            await using var services = new LauncherServices(directory.Paths, new("pt-BR", true), ["dotnet", typeof(App).Assembly.Location]);
            var window = new MainWindow();
            var model = new MainViewModel(services, window);
            window.DataContext = model;
            await model.InitializeAsync();
            window.Show(); Dispatcher.UIThread.RunJobs();
            await Eventually(() => services.Covers.LoadedCount == 2);
            await Eventually(() => window.GetVisualDescendants().OfType<InstanceCoverView>().Count(v => v.FindControl<Image>("Picture")!.Source is not null) == 2);
            void Screenshot(string name)
            {
                Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var bitmap = window.CaptureRenderedFrame();
                Assert.NotNull(bitmap);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
                { Directory.CreateDirectory(output); bitmap.Save(Path.Combine(output, name)); }
            }
            Screenshot("orion-instance-covers.png");
            model.Page = "about"; Dispatcher.UIThread.RunJobs();
            await Eventually(() => services.Covers.LoadedCount == 0);
            var maintainers = window.GetVisualDescendants().OfType<Button>().Where(b => b.Command == model.OpenMaintainerCommand).ToArray();
            Assert.Equal(["raonygamer", "yPerfectBR"], maintainers.Select(b => b.CommandParameter!.ToString()!).Order().ToArray());
            model.Page = "library";
            model.EditAppearance(model.Instances.Single(i => i.Instance.Id == first.Id));
            Assert.Equal(4, model.LaunchEditor!.SelectedTab);
            model.LaunchEditor.SelectedCover = model.LaunchEditor.CoverChoices.Single(c => c.Id == "jungle");
            Screenshot("orion-edit-cover.png");
            model.CloseLaunchOptionsCommand.Execute(null);
            Assert.Equal("mountains", (await repository.GetAsync(first.Id)).CoverId);
            model.EditAppearance(model.Instances.Single(i => i.Instance.Id == first.Id));
            model.LaunchEditor!.SelectedCover = model.LaunchEditor.CoverChoices.Single(c => c.Id == "jungle");
            await model.SaveLaunchOptionsCommand.ExecuteAsync(null);
            Assert.False(model.HasError);
            Assert.Equal("jungle", (await repository.GetAsync(first.Id)).CoverId);
            model.EditAppearance(model.Instances.Single(i => i.Instance.Id == first.Id));
            model.LaunchEditor!.SelectedCover = model.LaunchEditor.CoverChoices[0];
            await model.SaveLaunchOptionsCommand.ExecuteAsync(null);
            Assert.Null((await repository.GetAsync(first.Id)).CoverId);
            Assert.Equal("lush-caves", (await repository.GetAsync(second.Id)).CoverId);
            window.Close();
            await Eventually(() => services.Covers.LoadedCount == 0);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PublishedCoverUrlsCanBeDownloadedAndDecoded()
    {
        if (Environment.GetEnvironmentVariable("ORION_VALIDATE_COVERS") != "1") return;
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var store = new CoverStore(http, Path.Combine(directory.Root, "covers"));
            foreach (var cover in InstanceCovers.All)
            {
                using var image = await store.AcquireAsync(cover.Id, CancellationToken.None);
                Assert.NotNull(image);
                Assert.Equal(320, image.Bitmap.PixelSize.Width);
                Assert.True(image.Bitmap.PixelSize.Height <= 320);
            }
            Assert.Equal(0, store.LoadedCount);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MaintainerLinksUseTheExactGitHubProfiles()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            await using var services = new LauncherServices(directory.Paths, new("en-US", true), ["orion"]);
            var links = new RecordingDialogs();
            var model = new MainViewModel(services, links);
            await model.OpenMaintainerCommand.ExecuteAsync("yPerfectBR");
            await model.OpenMaintainerCommand.ExecuteAsync("raonygamer");
            Assert.Equal(["https://github.com/yPerfectBR", "https://github.com/raonygamer"], links.Opened);
            await model.OpenMaintainerCommand.ExecuteAsync("invalid");
            Assert.True(model.HasError); Assert.Equal(2, links.Opened.Count);
            return true;
        }, CancellationToken.None);
    }
    private sealed class RecordingDialogs : IWindowDialogs
    {
        public List<string> Opened { get; } = [];
        public Task OpenWebLinkAsync(Uri uri) { Opened.Add(uri.AbsoluteUri); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string text, string confirm, string cancel) => Task.FromResult(false);
        public Task<string?> RenameAsync(string title, string name, string save, string cancel) => Task.FromResult<string?>(null);
        public Task OpenFolderAsync(string path) => Task.CompletedTask;
    }
}

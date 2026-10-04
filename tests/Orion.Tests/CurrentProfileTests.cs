using System.Net;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orion.Desktop;
using Orion.Desktop.Composition;
using Orion.Desktop.Content;
using Orion.Desktop.I18n;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Views;
using Orion.Domain;
using Orion.Infrastructure.Runtime;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class CurrentProfileTests
{
    private static readonly XboxAccount First = new(new string('a', 64), "first@example.test", true);
    private static readonly XboxAccount Second = new(new string('b', 64), "second@example.test", true);
    private const string PictureUrl = "https://images-eds-ssl.xboxlive.com/image?test=avatar";
    private static Bitmap Picture()
    {
        var result = new RenderTargetBitmap(new PixelSize(64, 64));
        using var context = result.CreateDrawingContext();
        context.FillRectangle(new SolidColorBrush(Color.Parse("#357D85")), new Rect(0, 0, 64, 64));
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#C5F8E4")), null, new Point(32, 24), 11, 11);
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#C5F8E4")), null, new Point(32, 62), 24, 21);
        return result;
    }

    [Fact]
    public async Task SwitchingAndSigningOutRejectLateResponsesAndKeepOfflineMetadata()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            var late = new TaskCompletionSource<XboxProfile?>();
            var text = new Localizer(); text.SetLanguage("en-US");
            using var model = new XboxProfileViewModel(text,
                (id, _) => Task.FromResult<XboxProfile?>(id == Second.Id ? new(id, "CachedPlayer", PictureUrl) : null),
                (id, _) => id == First.Id ? late.Task : Task.FromException<XboxProfile?>(new IOException("offline")),
                (_, _) => Task.FromResult<Bitmap?>(Picture()));
            var first = model.SetAccountAsync(First, true);
            await model.SetAccountAsync(Second, true);
            Assert.Equal("CachedPlayer", model.DisplayName); Assert.True(model.HasAvatar);
            late.SetResult(new(First.Id, "WrongPlayer", null)); await first;
            Assert.Equal("CachedPlayer", model.DisplayName);
            await model.SetAccountAsync(null, true);
            Assert.False(model.HasAvatar); Assert.False(model.HasAccount);
            Assert.Equal("Sign in", model.DisplayName);
            text.SetLanguage("pt-BR"); Assert.Equal("Entrar", model.DisplayName);
            await model.SetAccountAsync(null, false); Assert.Equal("Perfil indisponível", model.DisplayName);
            return true;
        }, default);
    }

    [Fact]
    public async Task LatePicturesCannotSurviveAnAccountChange()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            var late = new TaskCompletionSource<Bitmap?>();
            using var model = new XboxProfileViewModel(new Localizer(), (_, _) => Task.FromResult<XboxProfile?>(null),
                (id, _) => Task.FromResult<XboxProfile?>(new(id, "Player", PictureUrl)), (_, _) => late.Task);
            var request = model.SetAccountAsync(First, true);
            Assert.Equal("Player", model.DisplayName);
            await model.SetAccountAsync(null, true);
            late.SetResult(Picture()); await request;
            Assert.False(model.HasAvatar); Assert.False(model.HasAccount);
            return true;
        }, default);
    }

    [Fact]
    public async Task AvatarDownloadIsBoundedValidatedAndCachedAt64Pixels()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            using var bitmap = Picture(); using var bytes = new MemoryStream(); bitmap.Save(bytes);
            var count = 0;
            using var http = new HttpClient(new ReleaseTests.Handler(request =>
            {
                Assert.Contains("w=64", request.RequestUri!.Query); Assert.Contains("h=64", request.RequestUri.Query);
                count++; var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) };
                response.Content.Headers.ContentType = new("image/png"); return response;
            }));
            var store = new AvatarStore(http, directory.Paths.Cache);
            foreach (var url in new[] { "http://images.xboxlive.com/pic", "https://xboxlive.com.evil.test/pic", "file:///tmp/pic", "https://localhost/pic" })
                Assert.Null(await store.LoadAsync(url, default));
            Assert.Equal(0, count);
            using (var first = await store.LoadAsync(PictureUrl, default)) Assert.Equal(64, first!.PixelSize.Width);
            using (var cached = await store.LoadAsync(PictureUrl, default)) Assert.Equal(64, cached!.PixelSize.Width);
            Assert.Equal(1, count); Assert.Empty(Directory.GetFiles(directory.Paths.Cache, "*.part"));
            using var invalidHttp = new HttpClient(new ReleaseTests.Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("bad image") }));
            await Assert.ThrowsAsync<InvalidDataException>(() => new AvatarStore(invalidHttp, directory.Paths.Cache).LoadAsync(PictureUrl + "2", default));
            return true;
        }, default);
    }

    [Fact]
    public async Task SidebarShowsCachedProfileAcrossAllPagesAndOpensAccounts()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            await using var services = new LauncherServices(directory.Paths, new("pt-BR", true), ["dotnet", typeof(App).Assembly.Location]);
            var profile = new XboxProfile(First.Id, "PlayerWithALongGamertag", PictureUrl);
            await AtomicFile.WriteJsonAsync(Path.Combine(directory.Paths.Cache, "xbox-profiles", First.Id + ".json"), profile);
            Assert.Equal(profile, await services.Account.ReadCachedProfileAsync(First.Id, default));
            var avatars = Path.Combine(directory.Paths.Cache, "xbox-avatars"); Directory.CreateDirectory(avatars);
            using (var avatar = Picture()) avatar.Save(Path.Combine(avatars, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PictureUrl))) + ".png"));
            var window = new MainWindow { Width = 900, Height = 640 };
            var model = new MainViewModel(services, window); window.DataContext = model;
            await model.CurrentProfile.SetAccountAsync(First, true); window.Show();
            Assert.Equal(profile.Gamertag, model.CurrentProfile.DisplayName); Assert.True(model.CurrentProfile.HasAvatar);
            var indicator = window.FindControl<Button>("CurrentProfileButton")!;
            foreach (var page in new[] { "library", "account", "downloads", "settings", "about" })
            {
                model.Page = page; Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Assert.True(indicator.IsEffectivelyVisible); Assert.True(indicator.Bounds.Height >= 38);
                Assert.Equal(profile.Gamertag, window.FindControl<TextBlock>("CurrentProfileName")!.Text);
                Assert.NotNull(window.FindControl<Image>("CurrentProfileAvatar")!.Source);
            }
            if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
            {
                Directory.CreateDirectory(output);
                using var screenshot = window.CaptureRenderedFrame(); screenshot!.Save(Path.Combine(output, "orion-current-profile.png"));
            }
            indicator.Command!.Execute(indicator.CommandParameter); Assert.Equal("account", model.Page);
            window.Close(); Assert.False(model.CurrentProfile.HasAvatar);
            return true;
        }, default);
    }

    [Fact]
    public async Task ProfileCommandUsesPinnedIdentityAndCachesOnlyValidatedMetadata()
    {
        using var directory = new TestDirectory();
        var runtime = Path.Combine(directory.Paths.Tools, "xodus", "fixture"); Directory.CreateDirectory(runtime);
        var json = System.Text.Json.JsonSerializer.Serialize(new XboxProfile(First.Id, "RealGamertag", PictureUrl), AtomicFile.Json);
        var executable = Path.Combine(runtime, "xodus-cli");
        await File.WriteAllTextAsync(executable, $"#!/bin/sh\n[ \"$1\" = accounts ] && [ \"$2\" = profile ] && [ \"$3\" = {First.Id} ] || exit 1\nprintf '%s' '{json}'\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Copy(executable, Path.Combine(runtime, "xodus-service"));
        await AtomicFile.WriteJsonAsync(Path.Combine(directory.Paths.Tools, "xodus", "current.json"), new RuntimeInstallation("xodus", "0.6.0", runtime));
        await using var services = new LauncherServices(directory.Paths, new("en-US", true), []);
        var result = await services.Account.ReadProfileAsync(First.Id, default);
        Assert.Equal("RealGamertag", result!.Gamertag);
        Assert.Equal(result, await services.Account.ReadCachedProfileAsync(First.Id, default));
        await Assert.ThrowsAsync<ArgumentException>(() => services.Account.ReadProfileAsync("../invalid", default));
        await AtomicFile.WriteJsonAsync(Path.Combine(directory.Paths.Cache, "xbox-profiles", First.Id + ".json"), result with { Id = Second.Id });
        Assert.Null(await services.Account.ReadCachedProfileAsync(First.Id, default));
    }

    [Fact]
    public async Task AccountCardsAndBothEditorsShareProfilesAndPreservePinnedChoices()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            var runtime = Path.Combine(directory.Paths.Tools, "xodus", "fixture"); Directory.CreateDirectory(runtime);
            var accountFile = Path.Combine(runtime, "accounts.json");
            await AtomicFile.WriteJsonAsync(accountFile, new[] { First, Second with { Active = false } });
            foreach (var name in new[] { "xodus-cli", "xodus-service" })
            {
                var executable = Path.Combine(runtime, name);
                await File.WriteAllTextAsync(executable, $"#!/bin/sh\ncat '{accountFile}'\n");
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            // A cached-only fixture proves the picker works offline and never selects an account to query it.
            await AtomicFile.WriteJsonAsync(Path.Combine(directory.Paths.Tools, "xodus", "current.json"), new RuntimeInstallation("xodus", "0.4.0", runtime));
            var avatarDirectory = Path.Combine(directory.Paths.Cache, "xbox-avatars"); Directory.CreateDirectory(avatarDirectory);
            foreach (var (account, tag) in new[] { (First, "AlexExplorer"), (Second, "SteveBuilder") })
            {
                var url = PictureUrl + account.Id;
                await AtomicFile.WriteJsonAsync(Path.Combine(directory.Paths.Cache, "xbox-profiles", account.Id + ".json"), new XboxProfile(account.Id, tag, url));
                using var avatar = Picture();
                avatar.Save(Path.Combine(avatarDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".png"));
            }
            var instance = GameInstance.Create("Shared profile preview", "26.52.03", "Release") with { LaunchOptions = new() { AccountId = Second.Id } };
            await new InstanceRepository(directory.Paths).SaveAsync(instance);
            await using var services = new LauncherServices(directory.Paths, new("pt-BR", true), []);
            var window = new MainWindow(); var model = new MainViewModel(services, window); window.DataContext = model;
            await model.InitializeAsync(); await Task.WhenAll(model.Accounts.Select(a => a.ProfileLoad));
            Assert.False(model.HasError); window.Show(); model.Page = "account";
            var first = model.Accounts.Single(a => a.Id == First.Id).Profile;
            var second = model.Accounts.Single(a => a.Id == Second.Id).Profile;
            Assert.Equal("AlexExplorer", first.DisplayName); Assert.Equal("SteveBuilder", second.DisplayName);
            Assert.Same(first, model.CurrentProfile); Assert.True(first.HasAvatar); Assert.True(second.HasAvatar);
            void Render(string name)
            {
                Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
                {
                    Directory.CreateDirectory(output);
                    using var screenshot = window.CaptureRenderedFrame(); screenshot!.Save(Path.Combine(output, name));
                }
            }
            Render("orion-account-metadata.png");
            var avatars = window.GetVisualDescendants().OfType<ProfileAvatar>().Where(v => v.IsEffectivelyVisible).ToArray();
            Assert.Equal(2, avatars.Length);
            Assert.Contains(avatars, v => v.GetVisualDescendants().OfType<Image>().Any(i => ReferenceEquals(i.Source, second.Avatar)));
            model.Versions.Add(new("26.52.03", "Release", new Uri("https://example.test/game.msixvc")));
            await model.OpenCreateCommand.ExecuteAsync(null);
            Assert.Same(first, model.CreationEditor.AccountChoices[0].Profile);
            var choice = model.CreationEditor.AccountChoices.Single(a => a.Id == Second.Id);
            Assert.Same(second, choice.Profile); model.CreationEditor.SelectedAccount = choice;
            Assert.Equal(Second.Id, model.CreationEditor.Build().AccountId);
            Render("orion-create-account-metadata.png");
            var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "InstanceAccountPicker" && c.IsEffectivelyVisible);
            Assert.Contains(picker.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "SteveBuilder");
            Assert.Contains(picker.GetVisualDescendants().OfType<Image>(), i => ReferenceEquals(i.Source, second.Avatar));
            picker.IsDropDownOpen = true;
            Render("orion-account-picker-options.png");
            Assert.NotNull(picker.ContainerFromIndex(0));
            Assert.Contains(picker.ContainerFromIndex(0)!.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "AlexExplorer");
            picker.IsDropDownOpen = false;
            model.CloseCreateCommand.Execute(null); model.EditLaunchOptions(model.Instances.Single());
            Assert.Same(second, model.LaunchEditor!.SelectedAccount!.Profile);
            Render("orion-edit-account-metadata.png");
            await AtomicFile.WriteJsonAsync(accountFile, new[] { First with { Active = false }, Second });
            await model.InitializeAsync(); await Task.WhenAll(model.Accounts.Select(a => a.ProfileLoad));
            Assert.Equal("SteveBuilder", model.CurrentProfile.DisplayName);
            Render("orion-switched-account-metadata.png");
            Assert.Equal("SteveBuilder", window.FindControl<TextBlock>("CurrentProfileName")!.Text);
            Assert.Same(model.CurrentProfile, model.LaunchEditor.AccountChoices[0].Profile);
            Assert.Equal(Second.Id, model.LaunchEditor.Build().AccountId);
            Assert.False(first.HasAvatar); Assert.False(second.HasAvatar); // Replaced images are released.
            await AtomicFile.WriteJsonAsync(accountFile, new[] { First });
            await model.InitializeAsync(); await Task.WhenAll(model.Accounts.Select(a => a.ProfileLoad));
            Assert.Equal(Second.Id, model.LaunchEditor.Build().AccountId); // Removed pins never silently follow current.
            Assert.Null(model.LaunchEditor.SelectedAccount!.Profile);
            Assert.Equal(model.Text["MissingInstanceAccount"], model.LaunchEditor.SelectedAccount.Label);
            window.Close(); Assert.All(model.Accounts, card => Assert.False(card.Profile.HasAvatar));
            return true;
        }, default);
    }

    [Fact]
    public async Task PublishedRuntimeCanLoadTheLocalXboxProfileAndAvatar()
    {
        // Explicit opt-in: no real account/keyring access in the normal test suite.
        if (Environment.GetEnvironmentVariable("ORION_PROFILE_RUNTIME") is not { } runtime) return;
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            var local = AppPaths.Discover();
            var paths = new AppPaths(directory.Paths.Data, local.Config, directory.Paths.Cache, directory.Paths.Runtime, directory.Paths.Applications);
            if (runtime != "latest")
                await AtomicFile.WriteJsonAsync(Path.Combine(paths.Tools, "xodus", "current.json"), new RuntimeInstallation("xodus", "0.6.0", runtime));
            await using var services = new LauncherServices(paths, new("pt-BR", true), []);
            if (runtime == "latest")
            {
                var installed = await services.Runtimes.EnsureAsync(RuntimeDefinition.Xodus, null, default);
                Assert.True(Version.Parse(installed.Tag.TrimStart('v')) >= new Version(0, 6, 0));
            }
            var accounts = await services.Account.ReadInstalledAsync(default);
            var active = Assert.Single(accounts!, a => a.Active);
            var window = new MainWindow(); var model = new MainViewModel(services, window); window.DataContext = model;
            await model.InitializeAsync();
            await Task.WhenAll(model.Accounts.Select(a => a.ProfileLoad));
            Assert.NotEqual(active.Username, model.CurrentProfile.DisplayName);
            Assert.True(model.CurrentProfile.HasAvatar);
            Assert.Equal(64, model.CurrentProfile.Avatar!.PixelSize.Width);
            Assert.All(model.Accounts, card => Assert.True(card.Profile.HasGamertag));
            model.Page = "account"; window.Show();
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
            {
                Directory.CreateDirectory(output);
                using var screenshot = window.CaptureRenderedFrame(); screenshot!.Save(Path.Combine(output, "orion-current-profile-live.png"));
            }
            window.Close(); return true;
        }, default);
    }
}

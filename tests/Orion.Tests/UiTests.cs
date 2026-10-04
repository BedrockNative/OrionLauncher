using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Orion.Desktop;
using Orion.Desktop.Composition;
using Orion.Desktop.I18n;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Views;
using Orion.Domain;
using Orion.Infrastructure.Storage;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Games;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class UiTests
{
    [Fact]
    public async Task SavedAccountsAndLiveInstanceJournalRenderWithoutNetwork()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            var runtime = Path.Combine(directory.Paths.Tools, "xodus", "fixture");
            Directory.CreateDirectory(runtime);
            var accounts = "[{\"id\":\"" + new string('a', 64) + "\",\"username\":\"Alex@example.test\",\"active\":true},"
                + "{\"id\":\"" + new string('b', 64) + "\",\"username\":\"Steve@example.test\",\"active\":false}]";
            foreach (var name in new[] { "xodus-cli", "xodus-service" })
            {
                var executable = Path.Combine(runtime, name);
                await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf '%s' '" + accounts + "'\n");
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            await AtomicFile.WriteJsonAsync(Path.Combine(directory.Paths.Tools, "xodus", "current.json"), new RuntimeInstallation("xodus", "0.4.0", runtime));
            var repository = new InstanceRepository(directory.Paths);
            var instance = GameInstance.Create("Survival with friends", "26.52.03", "Release");
            await repository.SaveAsync(instance);
            await using var services = new LauncherServices(directory.Paths, new("pt-BR", true), ["dotnet", typeof(App).Assembly.Location]);
            var window = new MainWindow();
            var model = new MainViewModel(services, window);
            window.DataContext = model;
            await model.InitializeAsync();
            Assert.False(model.HasError);
            Assert.Equal(2, model.Accounts.Count);
            Assert.Contains("Alex@example.test", model.AccountStatus);
            Assert.Single(model.Accounts, a => a.Active);
            window.Show();
            model.Page = "account";
            void Screenshot(string name)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var bitmap = window.CaptureRenderedFrame();
                Assert.NotNull(bitmap);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
                { Directory.CreateDirectory(output); bitmap.Save(Path.Combine(output, name)); }
            }
            Screenshot("orion-saved-accounts.png");
            model.Page = "library";
            var journal = directory.Paths.InstanceLog(instance.Id);
            ProcessLog.Event(journal, "Orion", "Preparing the WineGDK prefix");
            ProcessLog.Event(journal, "wineboot", "Exited with code 0");
            ProcessLog.Event(journal, "Orion", "Playing Survival with friends");
            ProcessLog.Event(journal, "Xodus service", "Private socket ready");
            await model.ShowInstanceLogAsync(model.Instances.Single());
            Assert.True(model.Log.IsOpen);
            Assert.Contains("Private socket ready", model.Log.Content);
            ProcessLog.Event(journal, "WineGDK", "New output");
            await model.Log.RefreshAsync();
            Assert.Contains("New output", model.Log.Content);
            Screenshot("orion-instance-journal.png");
            var dock = window.FindControl<Grid>("LogDock")!;
            var handle = window.FindControl<Thumb>("LogResizeHandle")!;
            handle.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragDeltaEvent, Vector = new Vector(0, -2000) });
            Screenshot("orion-instance-journal-expanded.png");
            Assert.True(dock.Bounds.Height <= dock.MaxHeight + 1);
            var scroll = window.FindControl<ScrollViewer>("LibraryScroll")!;
            Assert.True(dock.TranslatePoint(new Point(0, 0), window)!.Value.Y >= scroll.TranslatePoint(new Point(0, 0), window)!.Value.Y + 276);
            handle.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragDeltaEvent, Vector = new Vector(0, 2000) });
            Screenshot("orion-instance-journal-compact.png");
            Assert.True(dock.Bounds.Height <= 141);
            model.Log.CloseCommand.Execute(null);
            Assert.False(model.Log.IsOpen);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, model.Text["Quit"]));
            var menuButton = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "•••"));
            Assert.Null(menuButton.Flyout);
            menuButton.Focus();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.ShowLaunchOptions);
            model.CloseLaunchOptionsCommand.Execute(null);
            model.Downloads.Add(new(new(instance, "https://example.test/package", DownloadState.Running, "Downloading package · 80 / 200 MiB", .4), model));
            model.Downloads.Add(new(new(GameInstance.Create("Creative", "26.52.03", "Release"), "https://example.test/package", DownloadState.Completed, Fraction: 1), model));
            model.Page = "downloads";
            Screenshot("orion-downloads.png");
            model.ShowCreate = true;
            Screenshot("orion-new-instance.png");
            Assert.Equal(3, model.CreationEditor.AccountChoices.Count);
            Assert.Null(model.CreationEditor.SelectedAccount!.Id);
            model.CreationEditor.SelectedAccount = model.CreationEditor.AccountChoices[2];
            model.CreationEditor.LaunchCommand = "prime-run %command%";
            Assert.Equal(new string('b', 64), model.CreationEditor.Build().AccountId);
            Assert.Equal("prime-run %command%", model.CreationEditor.Build().LaunchCommand);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, model.Text["Import"]));
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    [Fact]
    public async Task MainWindowRendersLibraryAndAllNavigationPages()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            var repository = new InstanceRepository(directory.Paths);
            await repository.SaveAsync(GameInstance.Create("Survival with friends", "1.26.0.2", "Release"));
            await repository.SaveAsync(GameInstance.Create("The next chapter", "1.26.10.20", "Preview"));
            await repository.SaveAsync(GameInstance.Create("Creative workshop", "1.21.130.3", "Release"));
            await using var services = new LauncherServices(directory.Paths, new("en-US", true), ["dotnet", typeof(App).Assembly.Location]);
            var window = new MainWindow();
            var model = new MainViewModel(services, window);
            window.DataContext = model;
            await model.InitializeAsync();
            Assert.False(model.HasError);
            Assert.Equal(3, model.Instances.Count);
            window.Show();
            foreach (var page in new[] { "library", "account", "settings", "about" })
            {
                model.Page = page;
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var bitmap = window.CaptureRenderedFrame();
                Assert.NotNull(bitmap);
                Assert.True(bitmap.PixelSize.Width >= 900);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
                {
                    Directory.CreateDirectory(output);
                    bitmap.Save(Path.Combine(output, $"orion-{page}.png"));
                }
            }
            model.Search = "friends";
            Assert.Single(model.Instances);
            model.Search = "missing";
            Assert.True(model.IsEmpty);
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LaunchOptionsEditorRendersValidatesSavesAndCancels()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            var repository = new InstanceRepository(directory.Paths);
            var instance = GameInstance.Create("Survival · custom launch", "1.26", "Release");
            await repository.SaveAsync(instance);
            await using var services = new LauncherServices(directory.Paths, new("pt-BR", true), ["dotnet", typeof(App).Assembly.Location]);
            var window = new MainWindow();
            var model = new MainViewModel(services, window);
            window.DataContext = model;
            await model.InitializeAsync();
            window.Show();
            model.EditLaunchOptions(model.Instances.Single());
            var editor = model.LaunchEditor!;
            Assert.False(editor.DesktopShortcut);
            editor.Name = "Renamed from settings";
            editor.DesktopShortcut = true;
            editor.ArgumentLines = "--example\nvalue with spaces";
            editor.EnvironmentLines = "WINEPREFIX=/wrong";
            await model.SaveLaunchOptionsCommand.ExecuteAsync(null);
            Assert.True(model.HasError);
            Assert.True(model.ShowLaunchOptions);
            editor.EnvironmentLines = "WINEDEBUG=-all\nMANGOHUD=1";
            editor.CustomResolution = true; editor.Width = "1920"; editor.Height = "1080"; editor.Fullscreen = true;
            Assert.Contains("em breve", model.Text["CustomResolution"]);
            Assert.Contains("não alteram", model.Text["ResolutionHint"]);
            model.Error = null;
            Dispatcher.UIThread.RunJobs();
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.IsEffectivelyVisible);
            for (var tab = 0; tab < 6; tab++)
            {
                tabs.SelectedIndex = tab;
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var bitmap = window.CaptureRenderedFrame();
                Assert.NotNull(bitmap);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
                { Directory.CreateDirectory(output); bitmap.Save(Path.Combine(output, $"orion-launch-options-{tab}.png")); }
            }
            await model.SaveLaunchOptionsCommand.ExecuteAsync(null);
            Assert.False(model.HasError);
            Assert.False(model.ShowLaunchOptions);
            var saved = (await repository.GetAsync(instance.Id)).LaunchOptions;
            Assert.Equal("Renamed from settings", (await repository.GetAsync(instance.Id)).Name);
            Assert.True((await repository.GetAsync(instance.Id)).DesktopShortcut);
            var shortcut = Path.Combine(directory.Paths.Applications, $"io.bedrocknative.orion.{instance.Id:N}.desktop");
            Assert.Contains("Renamed from settings", await File.ReadAllTextAsync(shortcut));
            Assert.Equal(new GameResolution(1920, 1080), saved.Resolution);
            Assert.True(saved.Fullscreen);
            Assert.Equal(["--example", "value with spaces"], saved.Arguments);
            model.EditLaunchOptions(model.Instances.Single());
            model.LaunchEditor!.ResetCommand.Execute(null);
            model.CloseLaunchOptionsCommand.Execute(null);
            Assert.Equal(saved.Resolution, (await repository.GetAsync(instance.Id)).LaunchOptions.Resolution);
            Assert.True(File.Exists(shortcut)); // Cancelling the editor does not remove the shortcut.
            model.EditLaunchOptions(model.Instances.Single());
            model.LaunchEditor!.DesktopShortcut = false;
            await model.SaveLaunchOptionsCommand.ExecuteAsync(null);
            Assert.False(File.Exists(shortcut));
            Assert.True(Directory.Exists(directory.Paths.Instance(instance.Id)));
            var card = model.Instances.Single();
            card.Running = true;
            model.EditLaunchOptions(card);
            Assert.True(model.ShowLaunchOptions);
            Assert.False(model.LaunchEditor!.CanEdit);
            model.LaunchEditor.SelectedTab = 5;
            Dispatcher.UIThread.RunJobs();
            var visibleButtons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToArray();
            Assert.False(visibleButtons.Single(b => Equals(b.Content, model.Text["Save"])).IsEffectivelyEnabled);
            Assert.False(visibleButtons.Single(b => Equals(b.Content, model.Text["Archive"])).IsEffectivelyEnabled);
            Assert.True(visibleButtons.Single(b => Equals(b.Content, model.Text["Folder"])).IsEffectivelyEnabled);
            var logButton = visibleButtons.Single(b => Equals(b.Content, model.Text["DetachLog"]));
            Assert.True(logButton.IsEffectivelyEnabled);
            ProcessLog.Append(directory.Paths.InstanceLog(instance.Id), "Settings log access");
            logButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var attempt = 0; attempt < 100 && window.LogWindows.Count == 0; attempt++) await Task.Delay(10);
            Assert.Contains("Settings log access", window.LogWindows.Single().Log.Content);
            Assert.True(model.ShowLaunchOptions);
            card.Running = false; model.LaunchEditor.RefreshState();
            Assert.True(model.LaunchEditor.CanEdit);
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CreationSavesAccountAndLaunchOptionsInTheDurableDownloadJob()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            await using var services = new LauncherServices(directory.Paths, new("en-US", true), ["dotnet", typeof(App).Assembly.Location]);
            var window = new MainWindow();
            var model = new MainViewModel(services, window);
            // Do not initialize the worker: this exercises queue persistence without a download.
            var account = new Orion.Infrastructure.Runtime.XboxAccount(new string('c', 64), "Saved account", false);
            using var profile = new XboxProfileViewModel(model.Text, services.Account.ReadCachedProfileAsync, services.Account.ReadProfileAsync, services.Avatars.LoadAsync);
            model.Accounts.Add(new(account, model, profile));
            var version = new GameVersion("26.52.03", "Release", new Uri("https://example.test/game.msixvc"));
            model.Versions.Add(version);
            await model.OpenCreateCommand.ExecuteAsync(null);
            model.SelectedVersion = version;
            model.NewName = "Created with options";
            model.CreationEditor.SelectedAccount = model.CreationEditor.AccountChoices.Single(a => a.Id == account.Id);
            model.CreationEditor.LaunchCommand = "prime-run %command%";
            model.CreationEditor.EnvironmentLines = "CUSTOM=value";
            model.CreationEditor.ShowLogOnLaunch = true;
            Assert.False(model.CreationEditor.DesktopShortcut);
            model.CreationEditor.DesktopShortcut = true;
            model.CreationEditor.SelectedCover = model.CreationEditor.CoverChoices.Single(c => c.Id == "jungle");
            await model.CreateCommand.ExecuteAsync(null);
            Assert.False(model.HasError);
            Assert.False(model.ShowCreate);
            Assert.True(model.IsDownloads);
            var job = services.Downloads.Snapshot.Single();
            var persisted = await AtomicFile.ReadJsonAsync<InstallationJob>(Path.Combine(directory.Paths.Download(job.Id), "job.json"));
            Assert.Equal(account.Id, persisted!.Instance.LaunchOptions.AccountId);
            Assert.Equal("prime-run %command%", persisted.Instance.LaunchOptions.LaunchCommand);
            Assert.Equal("value", persisted.Instance.LaunchOptions.Environment["CUSTOM"]);
            Assert.True(persisted.Instance.LaunchOptions.ShowLogOnLaunch);
            Assert.True(persisted.Instance.DesktopShortcut);
            Assert.Equal("jungle", persisted.Instance.CoverId);
            await model.OpenCreateCommand.ExecuteAsync(null);
            Assert.Null(model.CreationEditor.Build().AccountId);
            Assert.Empty(model.CreationEditor.Build().Environment);
            Assert.Null(model.CreationEditor.SelectedCover.Id);
            Assert.False(model.CreationEditor.DesktopShortcut);
            await model.StopAllAsync();
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DetachedLogsRefreshIndependentlyAndCopyOnlyTheirOwnOutput()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            var window = new MainWindow();
            window.Show();
            var model = new InstanceLogViewModel(new Localizer());
            var first = directory.Paths.InstanceLog(Guid.NewGuid());
            var second = directory.Paths.InstanceLog(Guid.NewGuid());
            ProcessLog.Append(first, "first instance");
            ProcessLog.Append(second, "second instance");
            await model.OpenAsync("First", first);
            string? copied = null;
            await model.CopyAsync(text => { copied = text; return Task.CompletedTask; });
            Assert.Equal("first instance", copied);
            await window.DetachLogAsync(model);
            Assert.False(model.IsOpen);
            var firstWindow = window.LogWindows.Single();
            Assert.True(firstWindow.Log.IsDetached);
            await model.OpenAsync("Second", second);
            await window.DetachLogAsync(model);
            Assert.Equal(2, window.LogWindows.Count);
            ProcessLog.Append(first, " more from first");
            await firstWindow.Log.RefreshAsync();
            await firstWindow.Log.CopyAsync(text => { copied = text; return Task.CompletedTask; });
            Assert.Contains("more from first", copied);
            Assert.DoesNotContain("second", copied);
            Assert.Equal(firstWindow.Log.Text["LogCopied"], firstWindow.Log.CopyStatus);
            await firstWindow.Log.CopyAsync(_ => Task.FromException(new IOException()));
            Assert.Equal(firstWindow.Log.Text["LogCopyError"], firstWindow.Log.CopyStatus);
            await firstWindow.Log.ClearAsync(() => Task.FromResult(false));
            Assert.Contains("first instance", File.ReadAllText(first));
            await firstWindow.Log.ClearAsync(() => Task.FromResult(true));
            Assert.Empty(firstWindow.Log.Content); Assert.Empty(File.ReadAllText(first));
            Assert.Equal("second instance", File.ReadAllText(second));
            ProcessLog.Append(first, "output after clear"); await firstWindow.Log.RefreshAsync();
            Assert.Equal("output after clear", firstWindow.Log.Content);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var bitmap = firstWindow.CaptureRenderedFrame();
            Assert.NotNull(bitmap);
            if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
            { Directory.CreateDirectory(output); bitmap.Save(Path.Combine(output, "orion-detached-log.png")); }
            await model.OpenAsync("First", first);
            await window.DetachLogAsync(model);
            Assert.Equal(2, window.LogWindows.Count);
            firstWindow.Log.Close();
            Assert.Single(window.LogWindows);
            Assert.True(window.LogWindows.Single().Log.IsOpen);
            window.CloseLogWindows();
            Assert.Empty(window.LogWindows);
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AboutOwnsBuildDetailsAndOpensTheOfflineChangelog()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory();
            await using var services = new LauncherServices(directory.Paths, new("pt-BR", true), ["dotnet", typeof(App).Assembly.Location]);
            var window = new MainWindow();
            var model = new MainViewModel(services, window);
            window.DataContext = model;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),
                b => b.IsEffectivelyVisible && b.Text == model.LauncherVersion);
            model.Page = "about";
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                b => b.IsEffectivelyVisible && b.Text == model.LauncherVersion);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(),
                b => b.IsEffectivelyVisible && b.Command == model.OpenChangelogCommand);
            var open = model.OpenChangelogCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            var dialog = window.OwnedWindows.OfType<ChangelogWindow>().Single();
            var notes = Assert.IsType<ChangelogViewModel>(dialog.DataContext);
            Assert.False(notes.SelectedEntry.IsLegacy);
            void Screenshot(Window target, string name)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var bitmap = target.CaptureRenderedFrame();
                Assert.NotNull(bitmap);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output)
                { Directory.CreateDirectory(output); bitmap.Save(Path.Combine(output, name)); }
            }
            Screenshot(window, "orion-about.png");
            Screenshot(dialog, "orion-changelog-current.png");
            notes.SelectedEntry = notes.Entries.First(entry => entry.IsLegacy);
            Assert.True(notes.SelectedEntry.IsLegacy);
            Assert.NotEmpty(notes.Blocks);
            Screenshot(dialog, "orion-changelog-legacy.png");
            dialog.Close();
            await open;
            Assert.False(model.HasError);
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public void SwitchingLanguageNotifiesBindingsAndHasFallback()
    {
        var localizer = new Localizer();
        var notified = false;
        localizer.PropertyChanged += (_, _) => notified = true;
        localizer.SetLanguage("pt-BR");
        Assert.True(notified);
        Assert.Equal("Jogar", localizer["Play"]);
        localizer.SetLanguage("unknown");
        Assert.Equal("Play", localizer["Play"]);
        Assert.Equal("MissingKey", localizer["MissingKey"]);
    }
}

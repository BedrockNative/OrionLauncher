using Avalonia.Headless;
using Avalonia.Controls;
using Orion.Desktop.Composition;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Views;
using Orion.Domain;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class InstanceFolderTests
{
    [Fact]
    public async Task FileManagerSelectionSurvivesLanguageRefreshAndAutosave()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var dir = new TestDirectory();
            await using var services = new LauncherServices(dir.Paths, new() { FileManager = "dolphin" }, ["orion"]);
            var model = new MainViewModel(services, new RecordingDialogs());
            var panel = new SettingsPanel { DataContext = model };
            var window = new Window { Content = panel };
            try
            {
                window.Show();
                var picker = panel.FindControl<ComboBox>("FileManagerPicker")!;
                Assert.Equal(1, picker.SelectedIndex);
                picker.SelectedIndex = 2;
                Assert.Equal("thunar", model.SelectedFileManager);
                var selected = picker.SelectedItem;
                model.LanguageIndex = 1;
                model.RefreshFileManagersCommand.Execute(null);
                Assert.Same(selected, picker.SelectedItem);
                Assert.StartsWith("Padrão do sistema", model.FileManagers[0].Label);
                await model.SaveSettingsCommand.ExecuteAsync(null);
                Assert.Equal("thunar", (await services.SettingsStore.LoadAsync()).FileManager);
                Assert.Equal("thunar", services.Settings.FileManager);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } screenshots)
                {
                    Directory.CreateDirectory(screenshots);
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var frame = window.CaptureRenderedFrame();
                    frame!.Save(Path.Combine(screenshots, "file-manager-settings.png"));
                }
            }
            finally { window.Close(); await model.StopAllAsync(); model.DisposeProfiles(); }
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("Release", "Minecraft Bedrock")]
    [InlineData("Preview", "Minecraft Bedrock Preview")]
    public async Task FreshStoragePreparesOnlySharedUserPacks(string channel, string edition)
    {
        using var dir = new TestDirectory();
        var instance = GameInstance.Create("Fixture", "26.40", channel);
        await new InstanceRepository(dir.Paths).SaveAsync(instance);
        var activity = new InstanceActivity();
        var service = new InstanceContentService(dir.Paths, activity);
        using (activity.Acquire(instance.Id))
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StorageFoldersAsync(instance.Id));
        var folder = Assert.Single(await service.StorageFoldersAsync(instance.Id));
        Assert.EndsWith($"{edition}/Users/Shared/games/com.mojang", folder.Id);
        Assert.True(Directory.Exists(Path.Combine(folder.Id, "resource_packs")));
        Assert.True(Directory.Exists(Path.Combine(folder.Id, "behavior_packs")));
        Assert.False(Directory.Exists(Path.Combine(dir.Paths.Instance(instance.Id), "game")));
        using (activity.Acquire(instance.Id))
            Assert.Equal(folder, Assert.Single(await service.StorageFoldersAsync(instance.Id)));
    }

    [Fact]
    public async Task StorageListsExistingProfilesAndRejectsExternalSymlinks()
    {
        using var dir = new TestDirectory(); var id = Guid.NewGuid();
        var users = ContentTests.Setup(dir, id, "12345"); ContentTests.Setup(dir, id, "67890");
        var service = new InstanceContentService(dir.Paths, new());
        var folders = await service.StorageFoldersAsync(id);
        Assert.Equal(3, folders.Count);
        Assert.All(folders, f => Assert.EndsWith("games/com.mojang", f.Id));
        Assert.Contains(folders, f => f.Id == Path.Combine(users, "12345/games/com.mojang"));
        var outside = Path.Combine(dir.Root, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(users, "external-profile"), outside);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.StorageFoldersAsync(id));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task ManagementOpensCorrectFoldersAllowsProfileChoiceAndReportsErrors()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var dir = new TestDirectory();
            await using var services = new LauncherServices(dir.Paths, new("en-US", true), ["orion"]);
            var instance = GameInstance.Create("Fixture", "26.40", "Release");
            await new InstanceRepository(dir.Paths).SaveAsync(instance);
            var dialogs = new RecordingDialogs(); var model = new MainViewModel(services, dialogs);
            try
            {
                var card = new InstanceCardViewModel(instance, model);
                await card.FolderCommand.ExecuteAsync(null);
                Assert.Equal(dir.Paths.Instance(instance.Id), Assert.Single(dialogs.Opened));
                await card.ContentFolderCommand.ExecuteAsync(null);
                Assert.EndsWith("Shared/games/com.mojang", dialogs.Opened[1]);
                var users = Directory.GetParent(dialogs.Opened[1])!.Parent!.Parent!.FullName;
                var profile = Path.Combine(users, "12345/games/com.mojang"); Directory.CreateDirectory(profile);
                // Multiple folders require a choice; cancel must never open another location.
                await card.ContentFolderCommand.ExecuteAsync(null); Assert.Equal(2, dialogs.Opened.Count);
                Assert.Equal(2, dialogs.Choices.Count);
                dialogs.Choice = profile;
                await card.ContentFolderCommand.ExecuteAsync(null); Assert.Equal(profile, dialogs.Opened[^1]);
                dialogs.Choice = dir.Root;
                await card.ContentFolderCommand.ExecuteAsync(null); Assert.Equal(3, dialogs.Opened.Count);
                dialogs.Fail = true;
                await card.FolderCommand.ExecuteAsync(null);
                Assert.True(model.HasError); Assert.Contains("File manager unavailable", model.Error);
                var window = new MainWindow();
                await Assert.ThrowsAsync<DirectoryNotFoundException>(() => window.OpenFolderAsync(Path.Combine(dir.Root, "missing")));
            }
            finally { await model.StopAllAsync(); model.DisposeProfiles(); }
            return true;
        }, CancellationToken.None);
    }

    private sealed class RecordingDialogs : IWindowDialogs
    {
        public List<string> Opened { get; } = [];
        public IReadOnlyList<FolderChoice> Choices { get; private set; } = [];
        public string? Choice { get; set; }
        public bool Fail { get; set; }
        public Task<bool> ConfirmAsync(string title, string text, string confirm, string cancel) => Task.FromResult(false);
        public Task OpenFolderAsync(string path)
        {
            if (Fail) throw new IOException("File manager unavailable");
            Opened.Add(path); return Task.CompletedTask;
        }
        public Task<string?> ChooseFolderAsync(string title, string hint, IReadOnlyList<FolderChoice> choices, string confirm, string cancel)
        { Choices = choices; return Task.FromResult(Choice); }
    }
}

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json;
using Orion.Domain;
using Orion.Desktop.Content;
using Orion.Desktop.I18n;
using Orion.Desktop.Theming;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Views;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Rtx;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class RtxUiTests
{
    private sealed class Dialogs : IWindowDialogs
    {
        public bool Accept = true;
        public List<(string Title, string Text, string Confirm)> Prompts { get; } = [];
        public Task<bool> ConfirmAsync(string title, string text, string confirm, string cancel)
        { Prompts.Add((title, text, confirm)); return Task.FromResult(Accept); }
        public Task OpenFolderAsync(string path) => Task.CompletedTask;
    }
    [Fact]
    public async Task BrowseFilterInstallRestoreAndNarrowConfigurationRender()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir); var catalog = new RtxTests.Catalog(); var activity = new InstanceActivity();
            var service = new RtxService(dir.Paths, activity, catalog, new InstanceContentService(dir.Paths, activity));
            using var http = new HttpClient(); using var covers = new CoverStore(http, dir.Paths.Cache);
            var dialogs = new Dialogs();
            var model = new RtxViewModel(catalog, service, () => Task.FromResult<IReadOnlyList<Orion.Domain.GameInstance>>([instance]), dialogs, new Localizer(), covers);
            var panel = new RtxPanel { DataContext = model }; var window = new Window { Content = panel, Width = 1180, Height = 780 };
            window.Show();
            void Shot(string name)
            {
                Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var bitmap = window.CaptureRenderedFrame(); Assert.NotNull(bitmap);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output) { Directory.CreateDirectory(output); bitmap.Save(Path.Combine(output, name)); }
            }
            try
            {
                await model.OpenAsync(); Assert.Null(model.Error); Assert.Equal(2, model.Presets.Count);
                Shot("rtx-browse.png");
                Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == model.Text["RtxExperimentalNotice"]);
                await model.OpenCreatorCommand.ExecuteAsync(null); Assert.Null(model.Error); Assert.True(model.IsCreator); Assert.True(model.CreatorReady);
                Assert.Equal(5, model.CreatorFields.Count);
                model.CreatorFields.First(f => f.Field.Name == "enabled").Toggle = false;
                Assert.False(model.CreatorFields.First(f => f.Field.Name == "power").Visible);
                Shot("rtx-native-creator.png");
                await model.BuildCreatorCommand.ExecuteAsync(null); Assert.Null(model.Error); Assert.Equal("build-test", model.Preset!.Id);
                Assert.False(model.HasInstalled); // Compile only previews: installation needs its own confirmation.
                Assert.False(model.CanInstall); // Untargeted compiler output cannot bypass the policy.
                model.BrowseCommand.Execute(null); model.Query = "refresh"; model.Query = "";
                model.Query = "cinem"; Assert.Single(model.Presets); model.Query = "no match"; Assert.True(model.Empty);
                model.Query = ""; model.Preset = model.Presets[0]; Assert.True(model.CanInstall);
                var olderPreset = model.Presets[0] with { Id = "older-target", GameVersion = "26.30" };
                model.Presets.Add(olderPreset); model.Preset = olderPreset; Assert.False(model.CanInstall);
                Assert.Equal(model.Text["RtxCompatibilityNewerGame"], model.CompatibilityText);
                Shot("rtx-blocked-newer-game.png");
                Assert.True(model.NeedsNewerRisk); Assert.False(model.AcceptNewerRisk);
                var riskBox = panel.FindControl<CheckBox>("NewerGameRisk")!;
                Assert.True(riskBox.IsEffectivelyVisible); Assert.False(riskBox.IsChecked);
                riskBox.IsChecked = true; Assert.True(model.AcceptNewerRisk); Assert.True(model.CanInstall);
                model.Instance = null; Assert.False(model.AcceptNewerRisk);
                model.Instance = instance; await model.OpenAsync();
                model.Preset = olderPreset;
                window.Height = 1200; Shot("rtx-newer-risk-checkbox.png"); window.Height = 780;
                model.AcceptNewerRisk = true;
                model.Preset = model.Presets[0]; Assert.False(model.AcceptNewerRisk);
                model.Preset = olderPreset; Assert.False(model.CanInstall);
                model.AcceptNewerRisk = true;
                await model.InstallCommand.ExecuteAsync(null); Assert.Null(model.Error);
                Assert.False(model.AcceptNewerRisk);
                Assert.Equal(instance.Version, (await service.InspectAsync(instance.Id)).Installation!.AcceptedNewerGameVersion);
                service.ValidateForLaunchUnderLease(instance.Id);
                await model.RestoreCommand.ExecuteAsync(null); Assert.Null(model.Error);
                model.Presets[0] = model.Presets[0] with { VersionFromAssetPath = true };
                model.Preset = model.Presets[0]; model.Presets.Remove(olderPreset);
                Assert.Equal(model.Text["RtxTargetInferred"], model.TargetSourceText);
                Shot("rtx-details.png");
                await model.InstallCommand.ExecuteAsync(null); Assert.Null(model.Error); Assert.True(model.HasInstalled);
                Assert.True(model.IsSelectedInstalled); Assert.Equal("Reinstall preset", model.InstallLabel);
                Assert.Equal(model.Preset!.Id, (await service.InspectAsync(instance.Id)).Installation!.PresetId);
                await model.InstallCommand.ExecuteAsync(null); Assert.Null(model.Error); Assert.True(model.IsSelectedInstalled);
                // A receipt created by the previous launcher still identifies Prizma-style selections.
                var receipt = (await service.InspectAsync(instance.Id)).Installation!;
                await File.WriteAllTextAsync(Path.Combine(dir.Paths.Instance(instance.Id), "rtx/current.json"), JsonSerializer.Serialize(receipt with { PresetId = null }));
                await model.OpenAsync(); Assert.Equal(1, catalog.Requests);
                Assert.True(model.IsSelectedInstalled);
                model.Preset = model.Presets[1]; Assert.False(model.IsSelectedInstalled); Assert.Equal("Install into instance", model.InstallLabel);
                model.Preset = model.Presets[0];
                model.Tab = 2; model.EnableOnLaunch = true; model.DisableVSync = true;
                await model.SaveConfigurationCommand.ExecuteAsync(null); Assert.True((await service.InspectAsync(instance.Id)).Configuration.EnableOnLaunch);
                window.Width = 680; ThemeManager.Apply("light", "theme", "orion"); Shot("rtx-configuration-light.png");
                model.Tab = 0; model.Preset = model.Presets[0]; Dispatcher.UIThread.RunJobs();
                Assert.False(panel.FindControl<Border>("Results")!.IsVisible); Shot("rtx-details-narrow.png");
                await model.RestoreCommand.ExecuteAsync(null); Assert.False(model.HasInstalled); Assert.Null(model.Error);
                Assert.False(model.IsSelectedInstalled); Assert.Equal("Install into instance", model.InstallLabel);

                var content = new InstanceContentService(dir.Paths, activity);
                await model.ResetPreferencesCommand.ExecuteAsync(null);
                Assert.False((await service.InspectAsync(instance.Id)).Configuration.EnableOnLaunch);
                var pack = RtxTests.Opus(); catalog.Textures = [pack];
                await content.ImportAsync(instance.Id, ContentTests.Zip(dir, ".mcpack", ("manifest.json", ContentTests.Manifest("Renamed texture", "resources", pack.PackId))), null);
                await model.BrowseTexturesCommand.ExecuteAsync(null);
                Assert.True(model.IsVanilla); Assert.False(model.IsBetter);
                Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == model.Text["RtxExperimentalNotice"]);
                model.ConfigureCommand.Execute(null); Dispatcher.UIThread.RunJobs();
                var visibleText = panel.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray();
                Assert.Contains("DLSS", visibleText);
                Assert.DoesNotContain(model.Text["RtxSetupTitle"], visibleText);
                Assert.DoesNotContain(model.Text["RtxCreatorLinks"], visibleText);
                Assert.DoesNotContain(model.Text["RtxRecovery"], visibleText);
                await model.BrowseTexturesCommand.ExecuteAsync(null);
                var row = Assert.Single(model.Textures); Assert.True(row.IsInstalled); Assert.False(row.CanInstall); Assert.Equal("Installed", row.ActionLabel);
                window.Width = 1180; Shot("rtx-installed-textures.png");
                var entry = Assert.Single(await service.InstalledTexturesAsync(instance.Id));
                await content.ArchiveAsync(instance.Id, entry.Id);
                await model.RefreshTexturesCommand.ExecuteAsync(null);
                Assert.True(Assert.Single(model.Textures).CanInstall); Assert.False(model.Textures[0].IsInstalled);
                var newerManifest = System.Text.Json.Nodes.JsonNode.Parse(ContentTests.Manifest("Opus", "resources", pack.PackId))!;
                newerManifest["header"]!["min_engine_version"] = new System.Text.Json.Nodes.JsonArray(1, 26, 52);
                catalog.TextureArchive = ContentTests.Zip(dir, ".mcpack", ("manifest.json", newerManifest.ToJsonString()));
                await model.InstallTextureCommand.ExecuteAsync(pack); Assert.Null(model.Error);
                var warning = Assert.Single(dialogs.Prompts, p => p.Title == model.Text["RtxVanillaMismatchTitle"]);
                Assert.Contains("1.26.52", warning.Text); Assert.Contains(instance.Name, warning.Text);
                Assert.Equal(model.Text["RtxAcknowledgeRisk"], warning.Confirm);
                Assert.True(Assert.Single(model.Textures).IsInstalled);
                model.Instance = null; Assert.False(model.Textures[0].CanInstall); Assert.False(model.CanInstall);
                Assert.DoesNotContain(panel.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.StartsWith("Rtx", StringComparison.Ordinal) == true);
            }
            finally { await model.StopAsync(); window.Close(); ThemeManager.Apply("dark", "theme", "orion"); }
            return true;
        }, CancellationToken.None);
    }
}

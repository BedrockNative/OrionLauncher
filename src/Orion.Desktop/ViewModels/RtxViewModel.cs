using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orion.Application;
using Orion.Desktop.Content;
using Orion.Desktop.I18n;
using Orion.Desktop.Views;
using Orion.Domain;
using Orion.Infrastructure.Rtx;
using Orion.Infrastructure.Linux;

namespace Orion.Desktop.ViewModels;

public sealed record RtxTextureItem(RtxTexturePack Pack, string? InstalledVersion, bool CanInstall, string ActionLabel)
{
    public string Name => Pack.Name;
    public string Version => Pack.Version;
    public bool IsInstalled => InstalledVersion is not null;
}

public partial class RtxViewModel(IRtxCatalog catalog, RtxService service, Func<Task<IReadOnlyList<GameInstance>>> listInstances,
    IWindowDialogs dialogs, Localizer text, CoverStore covers, RtxProtocolIntegration? protocol = null) : ObservableObject
{
    public Localizer Text { get; } = text;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBetter), nameof(IsVanilla), nameof(FamilyTitle), nameof(InstalledLabel), nameof(CanInstall), nameof(NeedsNewerRisk))]
    private RtxFamily family;
    public bool IsBetter => Family == RtxFamily.BetterRtx;
    public bool IsVanilla => Family == RtxFamily.VanillaRtx;
    public string FamilyTitle => IsBetter ? "BetterRTX" : "Vanilla RTX";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall), nameof(HasFamilyConflict))] private string? familyConflict;
    public bool HasFamilyConflict => FamilyConflict is not null;
    partial void OnFamilyChanged(RtxFamily value) { AcceptNewerRisk = false; Error = null; _ = LoadStateAsync(); }
    public CoverStore Covers { get; } = covers;
    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<RtxBatchResult> Outcomes { get; } = [];
    [ObservableProperty] private bool restoreAvailable;
    [ObservableProperty] private bool advancedVideo;
    [ObservableProperty] private string linkInput = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPending))] private string? pendingFile;
    public bool HasPending => PendingFile is not null;
    public bool HasOutcomes => Outcomes.Count > 0;
    [ObservableProperty] private string dlssStatus = "";
    [ObservableProperty] private bool canRestoreDlss;
    public bool CanRestore => RestoreAvailable;
    public bool ProtocolAvailable => protocol is not null;
    public bool ProtocolEnabled => protocol?.Registered == true;
    private IReadOnlyList<GameInstance> SelectedTargets() => Instance is { } instance ? [instance] : [];
    public ObservableCollection<RtxPreset> Presets { get; } = [];
    public ObservableCollection<RtxTextureItem> Textures { get; } = [];
    private IReadOnlyList<RtxTexturePack> catalogTextures = [];
    private IReadOnlyList<ContentEntry> installedTextures = [];
    private IReadOnlyList<RtxPreset> catalogPresets = [];
    private CancellationTokenSource? operation;
    private Task active = Task.CompletedTask;
    private readonly SemaphoreSlim incomingLinks = new(1, 1);
    private readonly SemaphoreSlim stateLoading = new(1, 1);
    private bool syncing;
    private bool stopped;
    private int instanceRevision;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool busy;
    public bool IsIdle => !Busy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? error;
    public bool HasError => !string.IsNullOrEmpty(Error);
    [ObservableProperty] private string status = "";
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool indeterminate = true;
    [ObservableProperty] private bool cached;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBrowse), nameof(IsTextures), nameof(IsConfiguration), nameof(IsCreator))] private int tab;
    public bool IsBrowse => Tab == 0;
    public bool IsTextures => Tab == 1;
    public bool IsConfiguration => Tab == 2;
    public bool IsCreator => Tab == 3;
    [ObservableProperty] private string query = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasInstance))] private GameInstance? instance;
    public bool HasInstance => Instance is not null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPreset), nameof(CompatibilityText), nameof(TargetSourceText), nameof(CanInstall), nameof(IsSelectedInstalled), nameof(InstallLabel), nameof(NeedsNewerRisk))] private RtxPreset? preset;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall))] private bool acceptNewerRisk;
    public bool NeedsNewerRisk => IsBetter && Preset is not null && SelectedTargets().Any(i => RtxCatalog.Compatibility(Preset.GameVersion, i.Version) == RtxCompatibility.NewerGame);
    private bool CanUseVersion(GameInstance item) => RtxCatalog.Compatibility(Preset?.GameVersion, item.Version) is RtxCompatibility.Matching
        || (AcceptNewerRisk && RtxCatalog.Compatibility(Preset?.GameVersion, item.Version) == RtxCompatibility.NewerGame);
    partial void OnPresetChanged(RtxPreset? value) => AcceptNewerRisk = false;
    public bool HasPreset => Preset is not null;
    public bool CanInstall => HasPreset && IsBetter && StateKnown && FamilyConflict is null && VerificationError is null && HasInstance
        && CanUseVersion(Instance!);
    public string TargetSourceText => Text[Preset?.VersionFromAssetPath == true ? "RtxTargetInferred" : "RtxTargetDeclared"];
    public bool IsSelectedInstalled => StateKnown && Installed is not null && Preset is not null
        && (Installed.PresetId is { } id ? id == Preset.Id : Installed.Name == Preset.Name && Installed.TargetVersion == Preset.GameVersion);
    public string InstallLabel => Text[IsSelectedInstalled ? "RtxReinstall" : "RtxInstall"];
    public bool Empty => Presets.Count == 0;
    [ObservableProperty] private bool enableOnLaunch;
    [ObservableProperty] private bool disableVSync;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InstalledLabel), nameof(HasInstalled), nameof(IsSelectedInstalled), nameof(InstallLabel))] private RtxInstallation? installed;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InstalledLabel), nameof(CanInstall), nameof(IsSelectedInstalled), nameof(InstallLabel))] private bool stateKnown;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall), nameof(HasInstalled))] private string? verificationError;
    public bool HasInstalled => Installed is not null && VerificationError is null;
    public string InstalledLabel => !StateKnown ? Text["RtxStateUnknown"] : IsBetter ? Installed?.Name ?? Text["RtxOriginal"]
        : installedTextures.Any(e => RtxService.IsVanillaTexture(e.PackId))
            ? string.Join(", ", installedTextures.Where(e => RtxService.IsVanillaTexture(e.PackId)).Select(e => e.Name)) : Text["RtxNoVanilla"];
    public string CompatibilityText => Preset is null || Instance is null ? Text["RtxChooseInstance"]
        : Text["RtxCompatibility" + RtxCatalog.Compatibility(Preset.GameVersion, Instance.Version)];

    partial void OnQueryChanged(string value) => Filter();
    private void Filter()
    {
        var selected = Preset?.Id;
        Presets.Clear(); foreach (var p in catalogPresets.Where(p => p.Name.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase))) Presets.Add(p);
        Preset = Presets.FirstOrDefault(p => p.Id == selected);
        OnPropertyChanged(nameof(Empty));
    }
    partial void OnInstanceChanged(GameInstance? value)
    {
        AcceptNewerRisk = false; OnPropertyChanged(nameof(NeedsNewerRisk));
        OnPropertyChanged(nameof(CanInstall)); OnPropertyChanged(nameof(CompatibilityText));
        if (!syncing) _ = LoadStateAsync();
    }
    private async Task LoadStateAsync()
    {
        var revision = ++instanceRevision; var selected = Instance; var selectedFamily = Family;
        FamilyConflict = null;
        StateKnown = false; Installed = null; VerificationError = null; RestoreAvailable = false; OnPropertyChanged(nameof(CanRestore));
        installedTextures = []; UpdateTextures(); EnableOnLaunch = false; DisableVSync = false; AdvancedVideo = false; CanRestoreDlss = false; DlssStatus = "";
        if (selected is null || stopped) return;
        await stateLoading.WaitAsync();
        try
        {
            if (revision != instanceRevision || stopped) return;
            var state = await service.InspectAsync(selected.Id, family: selectedFamily);
            var textures = await service.InstalledTexturesAsync(selected.Id);
            var dlss = await service.InspectDlssAsync(selected.Id);
            if (revision != instanceRevision || stopped) return;
            Installed = state.Installation; VerificationError = state.VerificationError;
            FamilyConflict = state.FamilyConflict;
            if (state.CompatibilityError is not null) Error = Text["RtxInstalledBlocked"] + "\n" + state.CompatibilityError;
            RestoreAvailable = state.CanRestore; OnPropertyChanged(nameof(CanRestore));
            DlssStatus = dlss.Error ?? dlss.Source ?? Text["RtxDlssOriginal"]; CanRestoreDlss = dlss.CanRestore;
            if (VerificationError is not null) Error = Text["RtxVerificationFailed"] + "\n" + VerificationError;
            EnableOnLaunch = state.Configuration.EnableOnLaunch; DisableVSync = state.Configuration.DisableVSync; AdvancedVideo = state.Configuration.AdvancedVideo;
            installedTextures = textures; StateKnown = true; OnPropertyChanged(nameof(InstalledLabel)); UpdateTextures();
        }
        catch (Exception ex) { if (revision == instanceRevision && !stopped) Error = ex.Message; }
        finally { stateLoading.Release(); }
    }
    public Task OpenAsync() => Run(async ct =>
    {
        AcceptNewerRisk = false;
        var selected = Instance?.Id; var instances = await listInstances();
        syncing = true;
        try { Instances.Clear(); foreach (var item in instances) Instances.Add(item); Instance = Instances.FirstOrDefault(i => i.Id == selected) ?? Instances.FirstOrDefault(); }
        finally { syncing = false; }
        await LoadStateAsync();
        if (catalogPresets.Count == 0) await FetchAsync(ct);
    });
    private async Task FetchAsync(CancellationToken ct)
    {
        catalogPresets = await catalog.PresetsAsync(ct); Cached = catalog.UsedCache; Filter();
        Status = Text["RtxReady"];
    }
    [RelayCommand] private Task RefreshAsync() => Run(FetchAsync);
    [RelayCommand] private void Browse() { Family = RtxFamily.BetterRtx; Tab = 0; }
    [RelayCommand] private void Configure() => Tab = 2;
    [RelayCommand] private Task BrowseTexturesAsync()
    {
        Family = RtxFamily.VanillaRtx;
        Tab = 1;
        return RefreshTexturesAsync();
    }
    [RelayCommand] private Task RefreshTexturesAsync() => Run(async ct =>
    {
        catalogTextures = await catalog.TexturesAsync(ct); await LoadStateAsync(); UpdateTextures();
        Status = Text["RtxReady"];
    });
    private void UpdateTextures()
    {
        Textures.Clear();
        foreach (var pack in catalogTextures)
        {
            var entry = installedTextures.FirstOrDefault(e => pack.PackId is not null && string.Equals(e.PackId, pack.PackId, StringComparison.OrdinalIgnoreCase));
            Textures.Add(new(pack, entry?.Version, StateKnown && FamilyConflict is null && HasInstance && entry is null,
                Text[entry is not null ? "RtxAlreadyInstalled" : StateKnown ? "RtxInstall" : "RtxStateUnknown"]));
        }
    }
    [RelayCommand] private Task VerifyAsync() => Run(async _ =>
    {
        await LoadStateAsync(); Status = Text[StateKnown && VerificationError is null ? "RtxVerified" : "RtxFailed"];
    });
    [RelayCommand] private void ClosePreset() => Preset = null;
    [RelayCommand] private void Cancel() => operation?.Cancel();
    [RelayCommand] private Task InstallAsync() => Run(async ct =>
    {
        var preset = Preset ?? throw new InvalidOperationException(Text["RtxChoosePreset"]);
        var acceptRisk = AcceptNewerRisk;
        try
        {
            await BatchAsync(InstallLabel, preset.Name + "\n" + Text["RtxBatchCompatibility"] + "\n" + Text["RtxInstallWarning"],
                (instance, token) => service.InstallAsync(instance, preset, acceptRisk, Reporter(), token), ct);
        }
        finally { AcceptNewerRisk = false; }
    });
    [RelayCommand] private Task RestoreAsync() => Run(async ct =>
    {
        await BatchAsync(Text["RtxRestore"], Text["RtxSafeRecoveryHint"], (instance, token) => service.RestoreAsync(instance.Id, token), ct);
    });
    [RelayCommand] private Task SaveConfigurationAsync() => Run(async ct =>
    {
        var configuration = new RtxConfiguration(EnableOnLaunch, DisableVSync, AdvancedVideo);
        var selectedFamily = Family;
        await BatchAsync(Text["RtxSaveConfiguration"], Text["RtxLaunchHint"], (instance, token) => service.ConfigureAsync(instance.Id, configuration, token, selectedFamily), ct);
    });
    [RelayCommand] private Task ResetPreferencesAsync() => Run(async ct =>
    {
        var selectedFamily = Family;
        await BatchAsync(Text["RtxResetPreferences"], FamilyTitle, (instance, token) => service.ConfigureAsync(instance.Id, new(), token, selectedFamily), ct);
    });
    [RelayCommand] private Task InstallTextureAsync(RtxTexturePack pack) => Run(async ct =>
    {
        await BatchAsync(Text["RtxInstall"], pack.Name + "\n" + Text["RtxTextureHint"],
            (instance, token) => service.InstallTextureAsync(instance.Id, pack, Reporter(), token,
                (minimum, version) => dialogs.ConfirmAsync(Text["RtxVanillaMismatchTitle"],
                    string.Format(Text["RtxVanillaMismatch"], instance.Name, version, string.IsNullOrWhiteSpace(minimum) ? Text["RtxStateUnknown"] : minimum),
                    Text["RtxAcknowledgeRisk"], Text["Cancel"])), ct);
    });
    public Task ImportAsync(string archive) => Run(async ct =>
    {
        await BatchAsync(Text["RtxImport"], Path.GetFileName(archive) + "\n" + Text["RtxImportVersionHint"] + "\n" + Text["RtxInstallWarning"],
            (instance, token) => service.ImportAsync(instance, archive, true, token), ct);
    });
    public Task ImportMaterialsAsync(IReadOnlyList<string> files) => Run(async ct =>
    {
        await BatchAsync(Text["RtxImportMaterials"], Text["RtxCompatibilityUnknown"] + "\n" + Text["RtxInstallWarning"],
            (instance, token) => service.ImportMaterialsAsync(instance, files, true, token), ct);
    });
    private async Task BatchAsync(string title, string hint, Func<GameInstance, CancellationToken, Task> action, CancellationToken ct)
    {
        var targets = SelectedTargets();
        if (targets.Count == 0) throw new InvalidOperationException(Text["RtxChooseTargets"]);
        if (!await dialogs.ConfirmAsync(title, string.Join("\n", targets.Select(i => i.Name + " · " + i.Version)) + "\n\n" + hint, title, Text["Cancel"])) return;
        SetOutcomes(await RtxBatch.RunAsync(targets, action, ct)); await LoadStateAsync();
    }
    private void SetOutcomes(IReadOnlyList<RtxBatchResult> results)
    {
        Outcomes.Clear(); foreach (var result in results) Outcomes.Add(result);
        OnPropertyChanged(nameof(HasOutcomes));
        Status = string.Format(Text["RtxBatchSummary"], results.Count(r => r.Success), results.Count);
        if (results.Any(r => !r.Success)) Error = string.Join("\n", results.Where(r => !r.Success).Select(r => r.Name + ": " + r.Error));
    }
    public Task ExportAsync(string directory, bool original) => Run(ct => BatchAsync(Text["RtxExport"], Text["RtxExportHint"],
        (instance, token) => service.ExportAsync(instance.Id, Path.Combine(directory, $"orion-{instance.Id:N}-{(original ? "original" : "preset")}-{Guid.NewGuid():N}.rtpack"), original, token), ct));
    public Task ImportDlssAsync(string file) => Run(ct => BatchAsync(Text["RtxDlssInstall"], Text["RtxDlssWarning"],
        (instance, token) => service.InstallDlssAsync(instance.Id, file, "Local DLL", token), ct));
    [RelayCommand] private Task RestoreDlssAsync() => Run(ct => BatchAsync(Text["RtxDlssRestore"], Text["RtxSafeRecoveryHint"],
        (instance, token) => service.RestoreDlssAsync(instance.Id, token), ct));
    [RelayCommand] private Task LatestDlssAsync() => Run(async ct =>
    {
        var targets = SelectedTargets(); if (targets.Count == 0) throw new InvalidOperationException(Text["RtxChooseTargets"]);
        if (!await dialogs.ConfirmAsync(Text["RtxDlssInstall"], string.Join("\n", targets.Select(i => i.Name)) + "\n\n" + Text["RtxDlssWarning"], Text["RtxDlssInstall"], Text["Cancel"])) return;
        SetOutcomes(await service.InstallLatestDlssAsync(targets, ct)); await LoadStateAsync();
    });
    [RelayCommand] private Task ResolveLinkAsync() => Run(async ct =>
    {
        var link = RtxLink.Parse(LinkInput.Trim()); var resolved = await catalog.ResolveLinkAsync(link, ct);
        if (Presets.All(p => p.Id != resolved.Id)) Presets.Add(resolved);
        Family = RtxFamily.BetterRtx; Preset = resolved; Tab = 0; Status = Text["RtxLinkReady"];
    });
    public async Task ReceiveAsync(string? link, string? file)
    {
        await incomingLinks.WaitAsync();
        try
        {
            await active; if (stopped) return;
            if (link is not null) { LinkInput = link; await ResolveLinkAsync(); }
            else if (file is not null) { Family = RtxFamily.BetterRtx; PendingFile = file; Tab = 2; Status = Text["RtxLinkReady"]; }
        }
        finally { incomingLinks.Release(); }
    }
    [RelayCommand] private Task ImportPendingAsync() => PendingFile is { } file ? ImportAsync(file) : Task.CompletedTask;
    [RelayCommand] private Task RegisterProtocolAsync() => Run(async _ =>
    {
        if (protocol is null) return;
        if (!await dialogs.ConfirmAsync(Text["RtxProtocol"], Text["RtxProtocolHint"], Text["RtxProtocol"], Text["Cancel"])) return;
        await protocol.SetEnabledAsync(true); OnPropertyChanged(nameof(ProtocolEnabled)); Status = Text["RtxProtocolHint"];
    });
    [RelayCommand] private Task UnregisterProtocolAsync() => Run(async _ =>
    {
        if (protocol is null) return;
        await protocol.SetEnabledAsync(false); OnPropertyChanged(nameof(ProtocolEnabled)); Status = Text["RtxReady"];
    });
    [RelayCommand] private Task OpenSetupAsync() => Run(_ => dialogs.OpenWebLinkAsync(new("https://bedrock.graphics/#setup")));
    [RelayCommand] private Task ReportIssueAsync() => Run(_ => dialogs.OpenWebLinkAsync(new("https://github.com/BedrockNative/OrionLauncher/issues")));
    [RelayCommand] private Task OpenWebsiteAsync() => Run(_ => dialogs.OpenWebLinkAsync(Preset?.Page ?? new("https://bedrock.graphics")));
    private IProgress<OperationProgress> Reporter() => new Progress<OperationProgress>(p => { Status = p.Message; Indeterminate = p.Fraction is null; Progress = (p.Fraction ?? 0) * 100; });
    private Task Run(Func<CancellationToken, Task> action)
    {
        if (Busy || stopped) return Task.CompletedTask;
        operation = new(); Busy = true; Error = null; Status = Text["RtxWorking"]; Indeterminate = true;
        return active = RunCore(action, operation);
    }
    private async Task RunCore(Func<CancellationToken, Task> action, CancellationTokenSource token)
    {
        try { await action(token.Token); }
        catch (OperationCanceledException) { Status = Text["RtxCancelled"]; }
        catch (Exception ex) { Error = ex.Message; Status = Text["RtxFailed"]; }
        finally { Busy = false; operation = null; token.Dispose(); }
    }
    public async Task StopAsync() { stopped = true; ++instanceRevision; operation?.Cancel(); await active; }
    public void RefreshLabels() { OnPropertyChanged(nameof(InstalledLabel)); OnPropertyChanged(nameof(CompatibilityText)); OnPropertyChanged(nameof(TargetSourceText)); OnPropertyChanged(nameof(InstallLabel)); UpdateTextures(); }
}

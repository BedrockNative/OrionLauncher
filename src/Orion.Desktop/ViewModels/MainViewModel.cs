using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orion.Desktop.Composition;
using Orion.Desktop.I18n;
using Orion.Desktop.Views;
using Orion.Domain;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Runtime;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Storage;
using Avalonia.Threading;
using Orion.Infrastructure.Linux;

namespace Orion.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly LauncherServices services;
    private readonly IWindowDialogs dialogs;
    private readonly SettingsAutoSave settingsSave;
    private bool stopping;
    private readonly Dictionary<Guid, CancellationTokenSource> games = [];
    private readonly HashSet<Task> activeTasks = [];
    private CancellationTokenSource? operation;
    private List<InstanceCardViewModel> allInstances = [];
    public Localizer Text { get; } = new();
    public string LauncherVersion => $"Orion {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)}";
    public string PlatformInfo => $"Linux · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
    public ObservableCollection<InstanceCardViewModel> Instances { get; } = [];
    public ObservableCollection<GameVersion> Versions { get; } = [];
    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = [];
    public bool DownloadsEmpty => Downloads.Count == 0;
    public InstanceLogViewModel Log { get; }
    public ThemeSettingsViewModel Appearance { get; }
    public ContentLibraryViewModel ContentLibrary { get; }
    public CurseForgeViewModel CurseForge { get; }
    public RtxViewModel Rtx { get; }
    public Orion.Desktop.Content.CoverStore Covers => services.Covers;
    public ObservableCollection<AccountCardViewModel> Accounts { get; } = [];
    [ObservableProperty] private XboxProfileViewModel currentProfile;
    [ObservableProperty] private string accountStatus = "";
    public string[] Languages { get; } = ["English", "Português (Brasil)"];
    public IReadOnlyList<FileManagerChoiceViewModel> FileManagers { get; } = DesktopFolderLauncher.Options.Select(o => new FileManagerChoiceViewModel(o)).ToArray();
    [ObservableProperty] private int fileManagerIndex;
    public string SelectedFileManager => DesktopFolderLauncher.Options[Math.Clamp(FileManagerIndex, 0, DesktopFolderLauncher.Options.Count - 1)].Id;
    public void ReportFolderError(Exception error) => Dispatcher.UIThread.Post(() => SetError(error));
    public bool HasActivity => IsBusy || ContentLibrary.Busy || CurseForge.IsBusy || Rtx.Busy || games.Count != 0 || services.Downloads.HasPending;
    public event Action? AttentionRequested;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsLibrary), nameof(IsAccount), nameof(IsSettings), nameof(IsAbout), nameof(IsDownloads), nameof(IsContent), nameof(IsCurseForge), nameof(IsRtx))]
    private string page = "library";
    public bool IsLibrary => Page == "library";
    public bool IsAccount => Page == "account";
    public bool IsSettings => Page == "settings";
    public bool IsAbout => Page == "about";
    public bool IsDownloads => Page == "downloads";
    public bool IsContent => Page == "content";
    public bool IsCurseForge => Page == "curseforge";
    public bool IsRtx => Page == "rtx";
    [ObservableProperty] private string search = "";
    [ObservableProperty] private bool isEmpty = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string status = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string? error;
    public bool HasError => !string.IsNullOrEmpty(Error);
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool indeterminate = true;
    [ObservableProperty] private bool showCreate;
    [ObservableProperty] private string newName = "";
    [ObservableProperty] private GameVersion? selectedVersion;
    [ObservableProperty] private int languageIndex;
    [ObservableProperty] private bool keepInBackground;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowLaunchOptions))] private LaunchOptionsViewModel? launchEditor;
    public bool ShowLaunchOptions => LaunchEditor is not null;
    [ObservableProperty] private LaunchOptionsViewModel creationEditor;
    private IEnumerable<XboxAccount> SavedAccounts => Accounts.Select(a => new XboxAccount(a.Id, a.Username, a.Active));

    public MainViewModel(LauncherServices services, IWindowDialogs dialogs)
    {
        this.services = services; this.dialogs = dialogs;
        Log = new(Text);
        languageIndex = services.Settings.Language == "pt-BR" ? 1 : 0;
        keepInBackground = services.Settings.KeepInBackground;
        fileManagerIndex = Math.Max(0, DesktopFolderLauncher.Options.ToList().FindIndex(o => o.Id == services.Settings.FileManager));
        Text.SetLanguage(services.Settings.Language);
        RefreshFileManagers();
        Appearance = new(services.Settings, Text);
        ContentLibrary = new(services.ContentLibrary, services.Content, () => services.Instances.ListAsync(), Text);
        CurseForge = new(services.CurseForge, new(services.ContentLibrary, services.Content, () => services.Instances.ListAsync(), Text), Text, services.ProjectCovers);
        Rtx = new(services.RtxCatalog, services.Rtx, () => services.Instances.ListAsync(), dialogs, Text, services.ProjectCovers, services.RtxLinks);
        Rtx.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Rtx.Busy)) OnPropertyChanged(nameof(HasActivity)); };
        CurseForge.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CurseForge.IsBusy)) OnPropertyChanged(nameof(HasActivity)); };
        ContentLibrary.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ContentLibrary.Busy)) OnPropertyChanged(nameof(HasActivity)); };
        settingsSave = new(async settings =>
        {
            await services.SettingsStore.SaveAsync(settings);
            services.Settings = settings;
            Appearance.AcceptChanges(settings);
        }, ex => Error = Text["SettingsSaveFailed"] + " " + ex.Message);
        Appearance.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Appearance.IsDirty)) QueueSettings(); };
        currentProfile = NewProfile();
        creationEditor = NewCreationEditor();
        Status = Text["Ready"];
        AccountStatus = Text["AccountsUnknown"];
        services.Downloads.Changed += DownloadChanged;
        Downloads.CollectionChanged += (_, _) => OnPropertyChanged(nameof(DownloadsEmpty));
    }

    public Task InitializeAsync() => WorkAsync(async ct =>
    {
        await ReloadAsync(); await services.Instances.SynchronizeAsync(ct);
        await services.ContentLibrary.ListAsync(ct);
        try { SetAccounts(await services.Account.ReadInstalledAsync(ct)); }
        catch (IOException) { AccountStatus = Text["AccountsUnknown"]; }
        catch (System.Text.Json.JsonException) { AccountStatus = Text["AccountsUnknown"]; }
        await services.Downloads.StartAsync();
    });

    [RelayCommand] private void Navigate(string value) => Page = value;
    partial void OnPageChanged(string value)
    {
        if (value == "content") _ = ContentLibrary.RefreshAsync();
        if (value == "curseforge") _ = CurseForge.OpenAsync();
        if (value == "rtx") _ = Rtx.OpenAsync();
    }
    [RelayCommand] private Task OpenChangelogAsync() => GuardAsync(() => dialogs.ShowChangelogAsync(Text, services.Settings.Language));
    [RelayCommand] private Task OpenMaintainerAsync(string name) => GuardAsync(async () =>
    {
        if (name is not ("yPerfectBR" or "raonygamer")) throw new ArgumentException("Unknown maintainer.");
        await dialogs.OpenWebLinkAsync(new Uri($"https://github.com/{name}"));
    });
    [RelayCommand] private void DismissError() => Error = null;
    [RelayCommand] private void CancelOperation() => operation?.Cancel();
    [RelayCommand] private void CloseCreate() { if (!IsBusy) ShowCreate = false; }

    [RelayCommand] private async Task OpenCreateAsync()
    {
        CreationEditor = NewCreationEditor();
        ShowCreate = true; NewName = "";
        if (Versions.Count == 0) await RefreshVersionsAsync();
    }

    [RelayCommand] private Task RefreshVersionsAsync() => WorkAsync(async ct =>
    {
        var catalog = await services.Catalog.GetAsync(ct);
        Versions.Clear(); foreach (var version in catalog) Versions.Add(version);
        SelectedVersion = Versions.FirstOrDefault(v => v.Channel == "Release");
        if (services.Catalog.UsedCachedData) Error = Text["CatalogOffline"];
    });

    [RelayCommand] private Task CreateAsync() => GuardAsync(async () =>
    {
        var version = SelectedVersion ?? throw new InvalidOperationException(Text["Version"]);
        var instance = GameInstance.Create(NewName, version.Version, version.Channel) with
        { LaunchOptions = CreationEditor.Build(), CoverId = CreationEditor.SelectedCover.Id, DesktopShortcut = CreationEditor.DesktopShortcut };
        await services.Downloads.EnqueueAsync(instance, version.Download.AbsoluteUri);
        ShowCreate = false;
        Page = "downloads";
    });

    private void DownloadChanged(InstallationJob job) => Dispatcher.UIThread.Post(async () =>
    {
        var card = Downloads.FirstOrDefault(d => d.Job.Id == job.Id);
        if (card is null) Downloads.Insert(0, new(job, this)); else card.Job = job;
        OnPropertyChanged(nameof(DownloadsEmpty)); OnPropertyChanged(nameof(HasActivity));
        if (job.State is DownloadState.Completed or DownloadState.Cancelled or DownloadState.Failed) await GuardAsync(ReloadAsync);
    });
    public Task CancelDownloadAsync(Guid id) => GuardAsync(() => services.Downloads.CancelAsync(id));
    public Task RetryDownloadAsync(Guid id) => GuardAsync(() => services.Downloads.RetryAsync(id));
    public Task ShowDownloadLogAsync(InstallationJob job) => Log.OpenAsync(job.Instance.Name, services.Paths.InstanceLog(job.Id));

    [RelayCommand] private Task LoginAsync() => WorkAsync(async ct =>
    {
        RequireNoGames(); InvalidateAccounts(); await services.Account.LoginAsync(Reporter(), ct);
        SetAccounts(await services.Account.RefreshAsync(Reporter(), ct));
    });
    [RelayCommand] private Task LogoutAsync() => WorkAsync(async ct =>
    {
        RequireNoGames(); InvalidateAccounts(); await services.Account.LogoutAsync(Reporter(), ct);
        SetAccounts([]);
    });

    [RelayCommand] private Task RefreshAccountsAsync() => WorkAsync(async ct =>
    {
        InvalidateAccounts();
        SetAccounts(await services.Account.RefreshAsync(Reporter(), ct));
    });

    public Task SelectAccountAsync(XboxAccount account) => WorkAsync(async ct =>
    {
        RequireNoGames();
        InvalidateAccounts();
        SetAccounts(await services.Account.ChangeAsync("select", account.Id, Reporter(), ct));
    });

    public Task RemoveAccountAsync(XboxAccount account) => WorkAsync(async ct =>
    {
        RequireNoGames();
        if (!await dialogs.ConfirmAsync(Text["RemoveAccount"], $"{account.Username}\n\n{Text["RemoveAccountHint"]}", Text["RemoveAccount"], Text["Cancel"])) return;
        InvalidateAccounts();
        SetAccounts(await services.Account.ChangeAsync("remove", account.Id, Reporter(), ct));
    });

    private void InvalidateAccounts()
    {
        DisposeProfiles();
        Accounts.Clear(); CurrentProfile = NewProfile(); AccountStatus = Text["AccountsUnknown"];
        _ = CurrentProfile.SetAccountAsync(null, false);
        UpdateAccountChoices();
    }

    private void SetAccounts(IReadOnlyList<XboxAccount>? accounts)
    {
        DisposeProfiles(); Accounts.Clear();
        // One profile and one decoded avatar per identity, shared by the sidebar,
        // account cards and both instance editors. No lookup on each dropdown opening.
        foreach (var account in (accounts ?? []).OrderByDescending(a => a.Active)) Accounts.Add(new(account, this, NewProfile()));
        CurrentProfile = Accounts.FirstOrDefault(a => a.Active)?.Profile ?? NewProfile();
        if (!CurrentProfile.HasAccount) _ = CurrentProfile.SetAccountAsync(null, accounts is not null);
        UpdateAccountChoices();
        AccountStatus = accounts is null ? Text["AccountsUpdateRequired"] : accounts.FirstOrDefault(a => a.Active) is { } active
            ? $"{Text["ActiveAccount"]}: {active.Username}" : Text["NoAccounts"];
    }

    private XboxProfileViewModel NewProfile() => new(Text, services.Account.ReadCachedProfileAsync, services.Account.ReadProfileAsync, services.Avatars.LoadAsync);
    private XboxProfileViewModel? ProfileFor(string? id) => id is null ? CurrentProfile : Accounts.FirstOrDefault(a => a.Id == id)?.Profile;
    public void DisposeProfiles()
    {
        CurrentProfile.Dispose();
        foreach (var account in Accounts) account.Profile.Dispose();
    }
    private void UpdateAccountChoices()
    {
        CreationEditor.SetAccounts(SavedAccounts, CreationEditor.SelectedAccount?.Id);
        LaunchEditor?.SetAccounts(SavedAccounts, LaunchEditor.SelectedAccount?.Id);
    }
    [RelayCommand] private Task UpdateRuntimesAsync() => WorkAsync(async ct =>
    {
        RequireNoGames();
        var xodus = await services.Runtimes.EnsureAsync(RuntimeDefinition.Xodus, Reporter(), ct, checkForUpdates: true);
        var wine = await services.Runtimes.EnsureAsync(RuntimeDefinition.WineGdk, Reporter(), ct, checkForUpdates: true);
        SetAccounts(await services.Account.ReadInstalledAsync(ct));
        Status = $"Xodus {xodus.Tag} · WineGDK {wine.Tag}";
    }, preserveStatus: true);

    private void QueueSettings()
    {
        if (stopping || settingsSave is null) return;
        if (!Appearance.Advanced.Light.ColorsValid || !Appearance.Advanced.Dark.ColorsValid)
            return;
        var settings = new LauncherSettings(LanguageIndex == 1 ? "pt-BR" : "en-US", KeepInBackground,
            Appearance.SelectedMode.Id, Appearance.SelectedPalette.Id, Appearance.SelectedVisual.Id)
            { Appearance = Appearance.Advanced.Snapshot(), FileManager = SelectedFileManager };
        settingsSave.Queue(settings);
    }
    partial void OnLanguageIndexChanged(int value)
    {
        Text.SetLanguage(value == 1 ? "pt-BR" : "en-US");
        RefreshFileManagers();
        Appearance.RefreshLabels();
        ContentLibrary.RefreshLabels();
        CurseForge.RefreshLabels();
        Rtx.RefreshLabels();
        CreationEditor.SetAccounts(SavedAccounts, CreationEditor.SelectedAccount?.Id);
        LaunchEditor?.SetAccounts(SavedAccounts, LaunchEditor.SelectedAccount?.Id);
        QueueSettings();
    }
    partial void OnKeepInBackgroundChanged(bool value) => QueueSettings();
    partial void OnFileManagerIndexChanged(int value) => QueueSettings();
    [RelayCommand] private void RefreshFileManagers()
    {
        var launcher = new DesktopFolderLauncher();
        foreach (var option in FileManagers) option.Refresh(Text, launcher);
    }
    // Also used by shutdown and integration tests; there is no Save button in the UI.
    [RelayCommand] private async Task SaveSettingsAsync() { QueueSettings(); await settingsSave.FlushAsync(); }
    [RelayCommand] private Task OpenLogsAsync() => OpenFolderAsync(services.Paths.Logs);
    [RelayCommand] private async Task ClearLogsAsync()
    {
        if (!await dialogs.ConfirmAsync(Text["ClearAllLogs"], Text["ClearAllLogsConfirm"], Text["ClearAllLogs"], Text["Cancel"])) return;
        await GuardAsync(async () =>
        {
            await Task.Run(() => Orion.Infrastructure.Processes.ProcessLog.ClearJournals(services.Paths.Logs));
            await Log.RefreshAsync();
            Status = Text["LogsCleared"];
        });
    }
    public Task OpenInstanceFolderAsync(Guid id) => OpenFolderAsync(services.Paths.Instance(id));
    public Task OpenInstanceContentFolderAsync(Guid id) => GuardAsync(async () =>
    {
        var folders = await services.Content.StorageFoldersAsync(id);
        if (folders.Count == 0) throw new DirectoryNotFoundException(Text["ContentStorageMissing"]);
        var choices = folders.Select(f => new FolderChoice(f.Id, f.Label)).ToArray();
        var selected = choices.Length == 1 ? choices[0].Path : await dialogs.ChooseFolderAsync(Text["OpenContentFolder"], Text["ContentStorageHint"], choices, Text["OpenContentFolder"], Text["Cancel"]);
        if (selected is not null && choices.Any(c => c.Path == selected)) await dialogs.OpenFolderAsync(selected);
    });
    public Task ShowInstanceLogAsync(InstanceCardViewModel card) => Log.OpenAsync(card.Name, services.Paths.InstanceLog(card.Instance.Id));
    private Task OpenFolderAsync(string path) => GuardAsync(() => dialogs.OpenFolderAsync(path));

    public Task ArchiveAsync(InstanceCardViewModel card) => WorkAsync(async ct =>
    {
        if (!await dialogs.ConfirmAsync(Text["ArchiveTitle"], Text["ArchiveText"], Text["ConfirmArchive"], Text["Cancel"])) return;
        await services.Instances.ArchiveAsync(card.Instance.Id, ct);
        LaunchEditor = null;
        await ReloadAsync();
    });

    public void EditLaunchOptions(InstanceCardViewModel card)
    {
        if (IsBusy) { Error = Text["Busy"]; return; }
        Error = null;
        try { LaunchEditor = new(card.Instance, Text, SavedAccounts, Covers, ProfileFor, card); }
        catch (Exception ex) { SetError(ex); }
    }

    [RelayCommand] private void CloseLaunchOptions() { if (!IsBusy) LaunchEditor = null; }
    public void EditAppearance(InstanceCardViewModel card)
    {
        EditLaunchOptions(card);
        if (LaunchEditor?.InstanceId == card.Instance.Id) LaunchEditor.SelectedTab = 4;
    }
    [RelayCommand] private Task SaveLaunchOptionsAsync() => WorkAsync(async ct =>
    {
        if (LaunchEditor is not { } editor) return;
        await services.Instances.SetConfigurationAsync(editor.InstanceId, editor.Name, editor.Build(), editor.SelectedCover.Id, editor.DesktopShortcut, ct);
        LaunchEditor = null;
        await ReloadAsync();
    });

    public Task PlayByIdAsync(Guid id) => GuardAsync(async () =>
    {
        var card = allInstances.FirstOrDefault(c => c.Instance.Id == id) ?? throw new InvalidOperationException("Instance not found.");
        await PlayAsync(card);
    });

    public async Task PlayAsync(InstanceCardViewModel card)
    {
        if (games.ContainsKey(card.Instance.Id)) return;
        if (IsBusy) { Error = Text["Busy"]; AttentionRequested?.Invoke(); return; }
        using var cancellation = new CancellationTokenSource();
        games.Add(card.Instance.Id, cancellation); card.Running = true;
        LaunchEditor?.RefreshState();
        Error = null;
        if (card.Instance.LaunchOptions.ShowLogOnLaunch) await ShowInstanceLogAsync(card);
        var task = services.Instances.PlayAsync(card.Instance.Id, Reporter(), cancellation.Token);
        activeTasks.Add(task);
        try { await task; Status = Text["Ready"]; }
        catch (OperationCanceledException) { Status = Text["Ready"]; }
        catch (Exception ex) { await ShowInstanceLogAsync(card); SetError(ex); }
        finally
        {
            activeTasks.Remove(task); games.Remove(card.Instance.Id); card.Running = false;
            foreach (var current in allInstances.Where(c => c.Instance.Id == card.Instance.Id)) current.Running = false;
            LaunchEditor?.RefreshState();
        }
    }

    public void Stop(Guid id) { if (games.TryGetValue(id, out var ct)) ct.Cancel(); }
    public Task ManageContentAsync(InstanceCardViewModel card, Func<ContentManagementViewModel, CancellationToken, Task> show) => WorkAsync(async ct =>
    {
        Status = Text["ContentTitle"];
        var model = new ContentManagementViewModel(services.Content, card.Instance.Id, card.Name, Text);
        try { await show(model, ct); }
        finally { await model.StopAsync(); }
    });
    public Task ManageContentAsync(Guid id, Func<ContentManagementViewModel, CancellationToken, Task> show)
    {
        var card = allInstances.FirstOrDefault(c => c.Instance.Id == id);
        return card is null ? Task.CompletedTask : ManageContentAsync(card, show);
    }
    public async Task StopAllAsync()
    {
        QueueSettings();
        stopping = true;
        await settingsSave.FlushAsync();
        await ContentLibrary.StopAsync();
        await CurseForge.StopAsync();
        await Rtx.StopAsync();
        Appearance.Dispose();
        DisposeProfiles();
        Log.Close();
        dialogs.CloseLogWindows();
        services.Downloads.Changed -= DownloadChanged;
        await services.Downloads.PauseAsync();
        operation?.Cancel(); foreach (var game in games.Values) game.Cancel();
        try { await Task.WhenAll(activeTasks.ToArray()); } catch (Exception) { /* Callers report their operation failures. */ }
    }

    private async Task ReloadAsync()
    {
        allInstances = (await services.Instances.ListAsync()).Select(i => new InstanceCardViewModel(i, this)
            { Running = games.ContainsKey(i.Id) }).ToList();
        Filter();
    }
    private LaunchOptionsViewModel NewCreationEditor() => new(GameInstance.Create("New instance", "pending", "Release"), Text, SavedAccounts, Covers, ProfileFor);
    partial void OnSearchChanged(string value) => Filter();
    private void Filter()
    {
        Instances.Clear();
        foreach (var card in allInstances.Where(c => c.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || c.Version.Contains(Search, StringComparison.OrdinalIgnoreCase))) Instances.Add(card);
        IsEmpty = Instances.Count == 0;
    }
    private void RequireNoGames() { if (games.Count != 0 || services.Downloads.HasPending) throw new InvalidOperationException(Text["RuntimeBusy"]); }
    private IProgress<OperationProgress> Reporter() => new Progress<OperationProgress>(p =>
    { Status = p.Message; Progress = (p.Fraction ?? 0) * 100; Indeterminate = p.Fraction is null; });
    private async Task WorkAsync(Func<CancellationToken, Task> work, bool preserveStatus = false)
    {
        if (IsBusy) return;
        IsBusy = true; Error = null; Status = Text["Installing"];
        using var cancellation = new CancellationTokenSource(); operation = cancellation;
        Task? task = null;
        try
        {
            task = work(cancellation.Token); activeTasks.Add(task); await task;
            if (!preserveStatus) Status = Text["Done"];
        }
        catch (OperationCanceledException) { Status = Text["Ready"]; }
        catch (Exception ex) { SetError(ex); }
        finally { if (task is not null) activeTasks.Remove(task); operation = null; IsBusy = false; }
    }
    private async Task GuardAsync(Func<Task> work) { try { await work(); } catch (Exception ex) { SetError(ex); } }
    private void SetError(Exception ex) { Error = ex.Message; Status = Text["Ready"]; AttentionRequested?.Invoke(); }
}

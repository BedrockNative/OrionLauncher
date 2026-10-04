using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orion.Desktop.I18n;
using Orion.Infrastructure.CurseForge;

namespace Orion.Desktop.ViewModels;

/// <summary>Independent discovery session. Navigation does not cancel an active transfer.</summary>
public partial class CurseForgeViewModel : ObservableObject
{
    private readonly CurseForgeClient curseForge;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private Task pending = Task.CompletedTask;
    private Task categoryNavigation = Task.CompletedTask;
    private bool refreshingLabels;
    private bool discoveryStarted;
    private int projectIndex, fileIndex, totalProjects, totalFiles;
    private sealed record ProjectSearch(string Query, int Category);
    private ProjectSearch? loadedSearch;
    public Localizer Text { get; }
    public Orion.Desktop.Content.CoverStore? Covers { get; }
    public CurseForgeDestinationsViewModel Destinations { get; }
    public bool HasCurseForge => curseForge.IsConfigured;
    public ObservableCollection<CfProject> Projects { get; } = [];
    public ObservableCollection<CfFile> Files { get; } = [];
    public ObservableCollection<CfCategory> Categories { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle), nameof(EmptyTitle))] private bool isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool downloading;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsManualDownload))] private ManualDownloadRequiredException? manualDownload;
    [ObservableProperty] private string downloadFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    [ObservableProperty] private string manualStatus = "";
    [ObservableProperty] private string? manualBrowserError;
    public bool IsManualDownload => ManualDownload is not null;
    public Func<Uri, Task<bool>>? OpenBrowserAsync { get; set; }
    [ObservableProperty] private string query = "";
    [ObservableProperty] private CfCategory? category;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasProject))] private CfProject? project;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DependencyHint), nameof(HasFile), nameof(IsWorldFile))] private CfFile? selectedFile;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EmptyTitle))] private bool hasSearched;
    public bool HasProject => Project is not null;
    public string ResultsTitle => Text[loadedSearch is null || loadedSearch.Query.Length == 0 ? "CfPopular" : "CfSearchResults"];
    public string ResultsCategory => Categories.FirstOrDefault(c => c.Id == (loadedSearch?.Category ?? Category?.Id))?.Name ?? "";
    public bool HasFile => SelectedFile is not null;
    public bool IsWorldFile => string.Equals(Path.GetExtension(SelectedFile?.FileName), ".mcworld", StringComparison.OrdinalIgnoreCase);
    public bool EmptyProjects => Projects.Count == 0;
    public string EmptyTitle => Text[IsBusy ? "ContentWorking" : HasSearched ? "CfNoResults" : "CfDiscoverTitle"];
    public bool CanNext => loadedSearch is not null && projectIndex + CurseForgeClient.PageSize < Math.Min(totalProjects, 10000);
    public bool CanPrevious => loadedSearch is not null && projectIndex > 0;
    public string DependencyHint => SelectedFile is { } f && f.Dependencies.Any(d => d.RelationType == 3)
        ? Text["CfDependencies"] + " " + string.Join(", ", f.Dependencies.Where(d => d.RelationType == 3).Select(d => d.ModId)) : "";
    public string PageLabel => Projects.Count == 0 ? "0" : $"{projectIndex + 1}–{projectIndex + Projects.Count} / {totalProjects}";

    public CurseForgeViewModel(CurseForgeClient curseForge, CurseForgeDestinationsViewModel destinations, Localizer text, Orion.Desktop.Content.CoverStore? covers = null)
    { this.curseForge = curseForge; Destinations = destinations; Text = text; Covers = covers; RefreshLabels(); }
    public void RefreshLabels()
    {
        refreshingLabels = true;
        try
        {
            var id = Category?.Id; Categories.Clear();
            Categories.Add(new(CurseForgeClient.AddonsClassId, Text["ContentAddons"], true));
            Categories.Add(new(CurseForgeClient.MapsClassId, Text["CfMaps"], true));
            Categories.Add(new(CurseForgeClient.TexturesClassId, Text["ContentTextures"], true));
            Category = Categories.FirstOrDefault(c => c.Id == id) ?? Categories[0];
        }
        finally { refreshingLabels = false; }
        OnPropertyChanged(nameof(EmptyTitle)); OnPropertyChanged(nameof(ResultsTitle)); OnPropertyChanged(nameof(ResultsCategory)); Destinations.RefreshLabels();
    }
    partial void OnCategoryChanged(CfCategory? oldValue, CfCategory? newValue)
    {
        OnPropertyChanged(nameof(ResultsCategory));
        if (refreshingLabels || lifetime.IsCancellationRequested || newValue is null || oldValue?.Id == newValue.Id
            || !HasCurseForge || loadedSearch is { Query.Length: > 0 }) return;
        // Selection before opening the page remains local. Once discovery is open,
        // a category change replaces its home, including the cursor and detail state.
        if (!discoveryStarted) return;
        ClearHome();
        categoryNavigation = NavigateCategoryAsync(newValue.Id, pending);
    }
    private void ClearHome()
    {
        Query = ""; loadedSearch = null; projectIndex = totalProjects = 0; HasSearched = false;
        Project = null; Projects.Clear(); NotifyResults();
    }
    private async Task NavigateCategoryAsync(int id, Task previous)
    {
        await previous;
        if (lifetime.IsCancellationRequested || Category?.Id != id) return;
        await HomeAsync();
    }
    public Task WaitForIdleAsync() => Task.WhenAll(pending, categoryNavigation);
    private void NotifyResults()
    {
        OnPropertyChanged(nameof(ResultsTitle)); OnPropertyChanged(nameof(ResultsCategory)); OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(EmptyProjects)); OnPropertyChanged(nameof(CanNext)); OnPropertyChanged(nameof(CanPrevious));
    }
    partial void OnProjectChanged(CfProject? value) { Files.Clear(); SelectedFile = null; fileIndex = 0; totalFiles = 0; }
    [RelayCommand] public Task RefreshAsync() => RunAsync(Destinations.RefreshAsync);
    public Task OpenAsync() => RunAsync(async ct =>
    {
        discoveryStarted = true;
        // Return to the user's current results when changing tabs; failures can be retried.
        await Destinations.RefreshAsync(ct);
        if (!HasSearched && HasCurseForge) await LoadProjectsAsync(new("", Category!.Id), 0, ct);
    });
    [RelayCommand] private Task HomeAsync() => RunAsync(async ct =>
    {
        Query = "";
        if (HasCurseForge) await LoadProjectsAsync(new("", Category!.Id), 0, ct);
    });
    [RelayCommand] private void CloseProject() => Project = null;
    private Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (IsBusy || lifetime.IsCancellationRequested) return Task.CompletedTask;
        pending = RunCoreAsync(work); return pending;
    }
    private async Task RunCoreAsync(Func<CancellationToken, Task> work)
    {
        IsBusy = true; Error = null; Status = Text["ContentWorking"];
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation = cancellation;
        try { await work(cancellation.Token); Status = Text["Done"]; }
        catch (OperationCanceledException) { Status = Text["Ready"]; }
        catch (Exception ex) { Error = ex.Message; Status = Text["Ready"]; }
        finally { operation = null; Downloading = false; IsBusy = false; }
    }
    [RelayCommand] private void Cancel() => operation?.Cancel();
    [RelayCommand] private Task SearchProjectsAsync() => RunAsync(async ct =>
    {
        var selected = Category ?? Categories[0];
        await LoadProjectsAsync(new(Query.Trim(), selected.Id), 0, ct);
    });
    private async Task LoadProjectsAsync(ProjectSearch search, int index, CancellationToken ct)
    {
        discoveryStarted = true;
        var result = await curseForge.SearchAsync(search.Query, search.Category, index, ct);
        ct.ThrowIfCancellationRequested();
        // A slow response from the previous home must never repopulate the new category.
        if (search.Query.Length == 0 && Category?.Id != search.Category) return;
        // Commit the page and its query together; failures preserve both the results and cursor.
        loadedSearch = search; projectIndex = index;
        Project = null; Projects.Clear(); foreach (var p in result.Data) Projects.Add(p);
        totalProjects = result.Pagination.TotalCount; HasSearched = true; NotifyResults();
    }
    [RelayCommand] private Task NextProjectsAsync() => RunAsync(async ct =>
    { if (loadedSearch is { } search && projectIndex + CurseForgeClient.PageSize < Math.Min(totalProjects, 10000))
        await LoadProjectsAsync(search, projectIndex + CurseForgeClient.PageSize, ct); });
    [RelayCommand] private Task PreviousProjectsAsync() => RunAsync(async ct =>
    { if (loadedSearch is { } search && projectIndex > 0) await LoadProjectsAsync(search, projectIndex - CurseForgeClient.PageSize, ct); });
    [RelayCommand] private Task LoadFilesAsync() => RunAsync(async ct =>
    {
        if (Project is null) return;
        var result = await curseForge.FilesAsync(Project.Id, 0, ct);
        Files.Clear(); foreach (var f in result.Data) Files.Add(f);
        totalFiles = result.Pagination.TotalCount; fileIndex = 0;
        SelectedFile = Files.FirstOrDefault();
    });
    [RelayCommand] private Task MoreFilesAsync() => RunAsync(async ct =>
    {
        if (Project is null || fileIndex + CurseForgeClient.PageSize >= Math.Min(totalFiles, 10000)) return;
        var result = await curseForge.FilesAsync(Project.Id, fileIndex + CurseForgeClient.PageSize, ct);
        foreach (var f in result.Data) Files.Add(f);
        fileIndex += CurseForgeClient.PageSize;
    });
    [RelayCommand] private Task InstallFileAsync() => RunAsync(async ct =>
    {
        if (Project is not { } project || SelectedFile is not { } file) return;
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".mcpack" or ".mcaddon" or ".mcworld"))
            throw new InvalidOperationException(Text["CfUnsupported"]);
        var destination = Destinations.Capture(extension == ".mcworld");
        var temp = Directory.CreateTempSubdirectory("orion-curseforge-");
        var path = Path.Combine(temp.FullName, "download" + extension);
        try
        {
            Downloading = true; Progress = 0;
            try { await curseForge.DownloadAsync(project.Id, file.Id, path, new Progress<double>(v => Progress = v * 100), ct); }
            catch (ManualDownloadRequiredException request)
            {
                Downloading = false; ManualDownload = request; ManualBrowserError = null; ManualStatus = Text["CfManualWaiting"];
                try
                {
                    await OpenManualPageAsync();
                    var states = new Progress<BrowserDownloadState>(state =>
                    {
                        if (!ReferenceEquals(ManualDownload, request)) return;
                        ManualStatus = Text[state switch
                        {
                            BrowserDownloadState.MissingFolder => "CfManualMissingFolder",
                            BrowserDownloadState.UnreadableFolder => "CfManualUnreadableFolder",
                            BrowserDownloadState.Verifying => "CfManualVerifying",
                            BrowserDownloadState.Mismatch => "CfManualMismatch",
                            _ => "CfManualWaiting"
                        }];
                    });
                    await new BrowserDownloadMonitor().WaitAsync(request.File, () => DownloadFolder, path, states, ct);
                }
                finally { ManualDownload = null; }
            }
            Downloading = false;
            await Destinations.InstallAsync(path, destination, ct, project.Name);
        }
        finally { if (File.Exists(path)) File.Delete(path); temp.Delete(); }
    });
    [RelayCommand] private async Task OpenManualPageAsync()
    {
        if (ManualDownload is not { } request || lifetime.IsCancellationRequested) return;
        var opened = false;
        try { if (OpenBrowserAsync is not null) opened = await OpenBrowserAsync(request.Page); }
        catch (Exception) { /* The monitor remains usable if the desktop browser fails. */ }
        if (ReferenceEquals(ManualDownload, request)) ManualBrowserError = opened ? null : Text["CfManualBrowserFailed"];
    }
    public async Task StopAsync() { lifetime.Cancel(); operation?.Cancel(); await WaitForIdleAsync(); }
}

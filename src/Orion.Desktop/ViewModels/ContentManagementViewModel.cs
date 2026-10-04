using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orion.Desktop.I18n;
using Orion.Domain;
using Orion.Infrastructure.Content;

namespace Orion.Desktop.ViewModels;

public sealed record ContentRow(ContentEntry Entry, string Detail) : IDisposable
{
    public LibraryPackRow Metadata { get; } = new(Entry);
    public void Dispose() => Metadata.Dispose();
    public string Name => Entry.Name;
    public string Symbol => Entry.Kind switch { ContentKind.World => "▧", ContentKind.Addon => "◇", _ => "◈" };
}

public partial class ContentManagementViewModel : ObservableObject
{
    private readonly InstanceContentService content;
    private readonly Guid instance;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private Task pending = Task.CompletedTask;
    private IReadOnlyList<ContentEntry> entries = [];
    public Localizer Text { get; }
    public string InstanceName { get; }
    public string[] Kinds => [Text["ContentWorlds"], Text["ContentAddons"], Text["ContentTextures"]];
    public ObservableCollection<ContentProfile> Profiles { get; } = [];
    public ObservableCollection<ContentRow> Items { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool downloading;
    [ObservableProperty] private int kindIndex;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private bool archived;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanArchive), nameof(CanRestore))] private ContentRow? selected;
    [ObservableProperty] private ContentProfile? profile;
    [ObservableProperty] private bool confirmArchive;
    public bool HasSelection => Selected is not null;
    public bool CanArchive => Selected is { Entry.Archived: false, Entry.Shared: false };
    public bool CanRestore => Selected is { Entry.Archived: true };
    public bool IsWorld => KindIndex == 0;
    public bool HasProfiles => Profiles.Count > 0;
    public bool NeedsWorldProfile => IsWorld && !HasProfiles;
    public bool Empty => Items.Count == 0;
    public string CountLabel => $"{Items.Count} {Text["ContentItems"]}";
    public ContentManagementViewModel(InstanceContentService content, Guid instance, string name, Localizer text)
    { this.content = content; this.instance = instance; InstanceName = name; Text = text; }
    partial void OnKindIndexChanged(int value) { OnPropertyChanged(nameof(IsWorld)); OnPropertyChanged(nameof(NeedsWorldProfile)); Filter(); }
    partial void OnSearchChanged(string value) => Filter();
    partial void OnArchivedChanged(bool value) => Filter();
    partial void OnProfileChanged(ContentProfile? value) => Filter();
    partial void OnSelectedChanged(ContentRow? value) => ConfirmArchive = false;
    private void Filter()
    {
        Selected = null; foreach (var row in Items) row.Dispose(); Items.Clear();
        foreach (var entry in entries.Where(e => (int)e.Kind == KindIndex && e.Archived == Archived
            && (!IsWorld || Profile is null || e.ProfileId == Profile.Id) && e.Name.Contains(Search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.ModifiedAt))
            Items.Add(new(entry, entry.Kind == ContentKind.World
                ? Text["ContentModified"] + ": " + entry.ModifiedAt?.ToLocalTime().ToString("g")
                : string.Join(" · ", new[] { entry.Version, entry.PackId, entry.Warning }.Where(s => !string.IsNullOrEmpty(s)))));
        OnPropertyChanged(nameof(Empty)); OnPropertyChanged(nameof(CountLabel));
    }
    private async Task ReloadAsync(CancellationToken ct)
    {
        var snapshot = await content.ListAsync(instance, ct);
        var profileId = Profile?.Id;
        Profiles.Clear(); foreach (var p in snapshot.Profiles) Profiles.Add(p);
        OnPropertyChanged(nameof(HasProfiles)); OnPropertyChanged(nameof(NeedsWorldProfile));
        Profile = Profiles.FirstOrDefault(p => p.Id == profileId) ?? (Profiles.Count == 1 ? Profiles[0] : null);
        entries = snapshot.Entries; Filter();
    }
    public Task RunAsync(Func<CancellationToken, Task> work)
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
    [RelayCommand] public Task RefreshAsync() => RunAsync(ReloadAsync);
    [RelayCommand] private void Cancel() => operation?.Cancel();
    [RelayCommand] private void RequestArchive() => ConfirmArchive = true;
    [RelayCommand] private void DismissArchive() => ConfirmArchive = false;
    [RelayCommand] private Task ArchiveAsync() => RunAsync(async ct =>
    {
        if (Selected is not { } row) return;
        await content.ArchiveAsync(instance, row.Entry.Id, ct); ConfirmArchive = false; await ReloadAsync(ct);
    });
    [RelayCommand] private Task RestoreAsync() => RunAsync(async ct =>
    {
        if (Selected is not { } row) return;
        await content.RestoreAsync(instance, row.Entry.Id, ct); await ReloadAsync(ct);
    });
    public Task ImportAsync(string path) => RunAsync(async ct =>
    { await content.ImportAsync(instance, path, Profile?.Id, ct); await ReloadAsync(ct); });
    public Task ExportAsync(string path) => RunAsync(ct => Selected is { } row
        ? content.ExportAsync(instance, row.Entry.Id, path, ct) : Task.CompletedTask);
    public string? SelectedFolder() => Selected is { } row ? content.GetFolder(instance, row.Entry.Id) : null;

    public async Task StopAsync() { lifetime.Cancel(); operation?.Cancel(); await pending; foreach (var row in Items) row.Dispose(); Items.Clear(); }
}

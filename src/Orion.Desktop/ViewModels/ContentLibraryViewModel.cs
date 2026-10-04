using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orion.Desktop.I18n;
using Orion.Domain;
using Orion.Infrastructure.Content;

namespace Orion.Desktop.ViewModels;

public partial class ContentTargetViewModel(GameInstance instance) : ObservableObject
{
    public GameInstance Instance { get; } = instance;
    public string Name => Instance.Name;
    public ObservableCollection<ContentProfile> Profiles { get; } = [];
    public bool HasProfiles => Profiles.Count > 0;
    [ObservableProperty] private ContentProfile? profile;
    [ObservableProperty] private string contentStatus = "";
    public IReadOnlyList<ContentEntry> Installed { get; set; } = [];
    public string? Warning { get; set; }
}
public sealed class LibraryRow(LibraryContent item, Localizer text) : ObservableObject, IDisposable
{
    private LibraryPackRow? preview;
    private bool previewLoaded;
    public LibraryContent Item { get; } = item;
    public string Name => Item.Name;
    public string Detail => string.Join(" · ", Item.Entries.Select(e => e.Version).Where(v => v.Length > 0).Distinct());
    public bool IsWorld => Item.Entries.Any(e => e.Kind == ContentKind.World);
    public string KindLabel => text[IsWorld ? "ContentWorlds" : Item.Entries.Any(e => e.Kind == ContentKind.Addon) ? "ContentAddons" : "ContentTextures"];
    public string PackCount => Item.Entries.Count > 1 ? $"×{Item.Entries.Count}" : "";
    public bool HasVersion => Detail.Length > 0;
    public Bitmap? Icon
    {
        get
        {
            if (previewLoaded) return preview?.Icon;
            previewLoaded = true;
            foreach (var entry in Item.Entries.OrderBy(e => e.Kind == ContentKind.Addon ? 0 : 1))
            {
                var candidate = new LibraryPackRow(entry);
                if (candidate.Icon is not null) { preview = candidate; break; }
                candidate.Dispose();
            }
            return preview?.Icon;
        }
    }
    public void RefreshLabels() => OnPropertyChanged(nameof(KindLabel));
    public void Dispose() { previewLoaded = true; preview?.Dispose(); preview = null; }
}
public sealed class LibraryPackRow : IDisposable
{
    public ContentEntry Entry { get; }
    public Bitmap? Icon { get; }
    public bool HasVersion => Entry.Version.Length > 0;
    public bool HasMinimum => Entry.MinimumEngineVersion.Length > 0;
    public bool IsWorld => Entry.Kind == ContentKind.World;
    public string ModifiedLabel => Entry.ModifiedAt?.ToLocalTime().ToString("g") ?? "";
    public LibraryPackRow(ContentEntry entry)
    {
        Entry = entry;
        try
        {
            Icon = Decode(entry.IconPath, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        if (Icon is null && IsWorld)
            try { Icon = Decode(entry.FallbackIconPath, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
    }
    private static Bitmap? Decode(string? path, bool monochrome)
    {
        if (path is null || new FileInfo(path) is not { Length: <= 4 * 1024 * 1024, LinkTarget: null }) return null;
        using var stream = File.OpenRead(path);
        using var codec = SkiaSharp.SKCodec.Create(stream);
        if (codec is null || codec.Info.Width < 1 || codec.Info.Height < 1 || (long)codec.Info.Width * codec.Info.Height > 4_000_000) return null;
        if (!monochrome)
        {
            stream.Position = 0;
            // Bound both dimensions, including tall icons, and avoid upscaling
            // small pack images just to shrink them again in the list.
            if (Math.Max(codec.Info.Width, codec.Info.Height) <= 192) return new Bitmap(stream);
            return codec.Info.Width >= codec.Info.Height
                ? Bitmap.DecodeToWidth(stream, 192) : Bitmap.DecodeToHeight(stream, 192);
        }
        using var decoded = new SkiaSharp.SKBitmap(codec.Info);
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SkiaSharp.SKCodecResult.Success) return null;
        using var thumbnail = new SkiaSharp.SKBitmap(192, 108);
        using (var canvas = new SkiaSharp.SKCanvas(thumbnail))
        using (var filter = SkiaSharp.SKColorFilter.CreateColorMatrix(new float[] {
            .2126f,.7152f,.0722f,0,0, .2126f,.7152f,.0722f,0,0, .2126f,.7152f,.0722f,0,0, 0,0,0,1,0 }))
        using (var paint = new SkiaSharp.SKPaint { ColorFilter = filter })
            canvas.DrawBitmap(decoded, new SkiaSharp.SKRect(0, 0, 192, 108), paint);
        using var image = SkiaSharp.SKImage.FromBitmap(thumbnail);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var encoded = data.AsStream();
        return new Bitmap(encoded);
    }
    public void Dispose() => Icon?.Dispose();
}

public partial class ContentLibraryViewModel : ObservableObject
{
    private readonly ContentLibraryService library;
    private readonly InstanceContentService local;
    private readonly Func<Task<IReadOnlyList<GameInstance>>> instances;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private Task pending = Task.CompletedTask;
    private IReadOnlyList<LibraryRow> catalog = [];
    public Localizer Text { get; }
    public ObservableCollection<LibraryRow> Items { get; } = [];
    public ObservableCollection<LibraryPackRow> Details { get; } = [];
    public ObservableCollection<ContentTargetViewModel> Targets { get; } = [];
    [ObservableProperty] private ContentTargetViewModel? target;
    public string[] Kinds => [Text["ContentAllKinds"], Text["ContentAddons"], Text["ContentTextures"], Text["ContentWorlds"]];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool busy;
    public bool IsIdle => !Busy;
    [ObservableProperty] private string? error;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string search = "";
    [ObservableProperty] private int kindIndex;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection), nameof(IsWorld), nameof(CanUnlink))] private LibraryRow? selected;
    public bool HasSelection => Selected is not null;
    public bool IsWorld => Selected?.IsWorld == true;
    public bool CanUnlink => Selected is { IsWorld: false };
    public bool Empty => Items.Count == 0;
    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) || KindIndex != 0;
    public string EmptyTitle => Text[HasFilters ? "ContentNoMatches" : "ContentEmpty"];
    public string EmptyHint => Text[HasFilters ? "ContentNoMatchesHint" : "ContentLibraryEmptyHint"];
    public ContentLibraryViewModel(ContentLibraryService library, InstanceContentService local, Func<Task<IReadOnlyList<GameInstance>>> instances, Localizer text)
    { this.library = library; this.local = local; this.instances = instances; Text = text; }
    partial void OnSearchChanged(string value) => Filter();
    partial void OnKindIndexChanged(int value) { if (value is < 0 or > 3) KindIndex = 0; else Filter(); }
    partial void OnSelectedChanged(LibraryRow? value)
    {
        foreach (var row in Details) row.Dispose(); Details.Clear();
        if (value is not null) foreach (var entry in value.Item.Entries) Details.Add(new(entry));
        UpdateTargetStatus();
    }
    private void UpdateTargetStatus()
    {
        foreach (var target in Targets)
        {
            var packs = Selected?.Item.Entries.Where(e => e.PackId is not null).ToArray() ?? [];
            var present = packs.Count(p => target.Installed.Any(e => !e.Archived && e.PackId == p.PackId));
            target.ContentStatus = packs.Length == 0 || target.Warning is not null ? "" : $"{Text["ContentPresent"]}: {present}/{packs.Length}";
        }
    }
    private void Filter()
    {
        var id = Selected?.Item.Id; Items.Clear();
        var kind = KindIndex switch { 1 => ContentKind.Addon, 2 => ContentKind.Texture, _ => ContentKind.World };
        foreach (var row in catalog.Where(i => (KindIndex == 0 || i.Item.Entries.Any(e => e.Kind == kind)) && (i.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || i.Item.Entries.Any(e => e.Name.Contains(Search, StringComparison.OrdinalIgnoreCase))))) Items.Add(row);
        Selected = Items.FirstOrDefault(i => i.Item.Id == id) ?? Items.FirstOrDefault(); OnPropertyChanged(nameof(Empty));
        OnPropertyChanged(nameof(HasFilters)); OnPropertyChanged(nameof(EmptyTitle)); OnPropertyChanged(nameof(EmptyHint));
    }
    private async Task Reload(CancellationToken ct)
    {
        var items = await library.ListAsync(ct);
        var previousRows = catalog;
        catalog = items.Select(i => new LibraryRow(i, Text)).ToArray(); Filter();
        foreach (var row in previousRows) row.Dispose();
        var selectedTarget = Target?.Instance.Id;
        var previous = Targets.ToDictionary(t => t.Instance.Id, t => t.Profile?.Id); Targets.Clear();
        foreach (var instance in await instances())
        {
            ct.ThrowIfCancellationRequested(); var target = new ContentTargetViewModel(instance);
            try
            {
                var snapshot = await local.ListAsync(instance.Id, ct); target.Installed = snapshot.Entries;
                foreach (var profile in snapshot.Profiles) target.Profiles.Add(profile);
                target.Profile = target.Profiles.FirstOrDefault(p => p.Id == previous.GetValueOrDefault(instance.Id))
                    ?? (target.Profiles.Count == 1 ? target.Profiles[0] : null);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { target.Warning = ex.Message; }
            Targets.Add(target);
        }
        Target = Targets.FirstOrDefault(t => t.Instance.Id == selectedTarget);
        UpdateTargetStatus();
    }
    private Task Run(Func<CancellationToken, Task> work)
    {
        if (Busy || lifetime.IsCancellationRequested) return Task.CompletedTask;
        pending = Core(work); return pending;
    }
    private async Task Core(Func<CancellationToken, Task> work)
    {
        Busy = true; Error = null; Status = Text["ContentWorking"];
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); operation = ct;
        try { await work(ct.Token); Status = Text["Done"]; }
        catch (OperationCanceledException) { Status = Text["Ready"]; }
        catch (Exception ex) { Error = ex.Message; Status = Text["Ready"]; }
        finally { operation = null; Busy = false; }
    }
    [RelayCommand] public Task RefreshAsync() => Run(Reload);
    [RelayCommand] private void Cancel() => operation?.Cancel();
    [RelayCommand] private void ClearFilters() { Search = ""; KindIndex = 0; }
    public Task ImportAsync(string file) => Run(async ct =>
    {
        var imported = await library.ImportAsync(file, ct);
        KindIndex = 0; Search = "";
        await Reload(ct); Target = null;
        Selected = Items.FirstOrDefault(i => i.Item.Id == imported.Id);
    });
    private ContentTarget[] Destinations()
    {
        if (Target is not { } target || !Targets.Contains(target)) throw new InvalidOperationException(Text["CfChooseDestination"]);
        if (target.Warning is not null) throw new InvalidOperationException(target.Warning);
        return [new(target.Instance.Id, target.Profile?.Id)];
    }
    [RelayCommand] private Task DistributeAsync() => Run(async ct =>
    {
        if (Selected is not { } row) return;
        await library.DistributeAsync(row.Item.Id, Destinations(), ct);
        await Reload(ct);
    });
    [RelayCommand] private Task UnlinkAsync() => Run(async ct =>
    {
        if (Selected is not { } row) return;
        await library.UnlinkAsync(row.Item.Id, Destinations(), ct);
        await Reload(ct);
    });
    public void RefreshLabels() { foreach (var row in catalog) row.RefreshLabels(); OnPropertyChanged(nameof(Kinds)); OnPropertyChanged(nameof(EmptyTitle)); OnPropertyChanged(nameof(EmptyHint)); UpdateTargetStatus(); }
    public async Task StopAsync()
    {
        lifetime.Cancel(); operation?.Cancel(); await pending;
        foreach (var row in Details) row.Dispose(); Details.Clear();
        Selected = null; Items.Clear();
        foreach (var row in catalog) row.Dispose(); catalog = [];
    }
}

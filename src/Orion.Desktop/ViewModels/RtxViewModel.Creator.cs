using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orion.Domain;

namespace Orion.Desktop.ViewModels;

public partial class RtxViewModel
{
    public ObservableCollection<RtxCreatorVersion> CreatorVersions { get; } = [];
    public ObservableCollection<RtxCreatorCategory> CreatorCategories { get; } = [];
    public ObservableCollection<RtxCreatorFieldViewModel> CreatorFields { get; } = [];
    private readonly List<RtxCreatorFieldViewModel> allCreatorFields = [];
    private RtxCreatorForm? creatorForm;
    private string? loadedCreatorVersion;
    [ObservableProperty] private RtxCreatorVersion? creatorVersion;
    [ObservableProperty] private RtxCreatorCategory? creatorCategory;
    [ObservableProperty] private string creatorQuery = "";
    [ObservableProperty] private bool creatorReady;
    partial void OnCreatorVersionChanged(RtxCreatorVersion? value) { CreatorReady = value?.Id == loadedCreatorVersion && creatorForm is not null; }
    partial void OnCreatorCategoryChanged(RtxCreatorCategory? value) => FilterCreator();
    partial void OnCreatorQueryChanged(string value) => FilterCreator();
    [RelayCommand] private Task OpenCreatorAsync()
    {
        Tab = 3;
        Family = RtxFamily.BetterRtx;
        return CreatorVersions.Count == 0 ? RefreshCreatorAsync() : Task.CompletedTask;
    }
    [RelayCommand] private Task RefreshCreatorAsync() => Run(async ct =>
    {
        var versions = await catalog.CreatorVersionsAsync(ct);
        var previous = CreatorVersion?.Id; CreatorVersions.Clear();
        foreach (var version in versions) CreatorVersions.Add(version);
        CreatorVersion = versions.FirstOrDefault(v => v.Id == previous) ?? versions.FirstOrDefault(v => v.IsDefault) ?? versions[0];
        await LoadCreatorCore(ct);
    });
    [RelayCommand] private Task LoadCreatorAsync() => Run(LoadCreatorCore);
    private async Task LoadCreatorCore(CancellationToken ct)
    {
        var version = CreatorVersion ?? throw new InvalidOperationException(Text["RtxCreatorChooseVersion"]);
        if (creatorForm is not null && !await dialogs.ConfirmAsync(Text["RtxCreatorLoad"], Text["RtxCreatorReplace"], Text["RtxCreatorLoad"], Text["Cancel"])) return;
        CreatorReady = false;
        var form = await catalog.CreatorFormAsync(version.Id, ct);
        creatorForm = form; loadedCreatorVersion = version.Id;
        CreatorCategories.Clear(); allCreatorFields.Clear();
        foreach (var category in form.Categories)
        {
            CreatorCategories.Add(category);
            foreach (var field in category.Fields)
            {
                var model = new RtxCreatorFieldViewModel(field);
                model.PropertyChanged += (_, e) => { if (e.PropertyName != nameof(RtxCreatorFieldViewModel.Visible)) UpdateCreatorVisibility(); };
                allCreatorFields.Add(model);
            }
        }
        CreatorCategory = CreatorCategories.FirstOrDefault(); FilterCreator(); UpdateCreatorVisibility(); CreatorReady = true;
        Status = string.Format(Text["RtxCreatorLoaded"], allCreatorFields.Count, version.Label);
    }
    private void FilterCreator()
    {
        CreatorFields.Clear();
        foreach (var field in allCreatorFields.Where(f => string.IsNullOrWhiteSpace(CreatorQuery)
            ? CreatorCategory?.Fields.Any(d => d.Name == f.Field.Name) == true
            : f.Label.Contains(CreatorQuery.Trim(), StringComparison.OrdinalIgnoreCase) || f.Description.Contains(CreatorQuery.Trim(), StringComparison.OrdinalIgnoreCase)))
            CreatorFields.Add(field);
    }
    private void UpdateCreatorVisibility()
    {
        if (creatorForm is null) return;
        var values = allCreatorFields.ToDictionary(f => f.Field.Name, f => f.Value());
        foreach (var field in allCreatorFields) field.Visible = creatorForm.IsVisible(field.Field, values);
    }
    [RelayCommand] private Task ResetCreatorAsync() => Run(async _ =>
    {
        if (!await dialogs.ConfirmAsync(Text["ResetDefaults"], Text["RtxCreatorReplace"], Text["ResetDefaults"], Text["Cancel"])) return;
        foreach (var field in allCreatorFields) field.Reset();
    });
    [RelayCommand] private Task BuildCreatorAsync() => Run(async ct =>
    {
        if (!CreatorReady || creatorForm is null || loadedCreatorVersion is null) return;
        if (!await dialogs.ConfirmAsync(Text["RtxCreatorBuild"], Text["RtxCreatorBuildHint"], Text["RtxCreatorBuild"], Text["Cancel"])) return;
        var preset = await catalog.BuildCreatorAsync(loadedCreatorVersion, creatorForm,
            allCreatorFields.ToDictionary(f => f.Field.Name, f => f.Value()), Reporter(), ct);
        Presets.Add(preset); Preset = preset; Tab = 0; Status = Text["RtxLinkReady"];
    });
    public Task ExportCreatorSettingsAsync(string directory) => Run(async ct =>
    {
        if (!CreatorReady || creatorForm is null || loadedCreatorVersion is null) return;
        var settings = creatorForm.SerializeSettings(allCreatorFields.ToDictionary(f => f.Field.Name, f => f.Value()));
        var path = Path.Combine(directory, $"BetterRTX-{loadedCreatorVersion}-{Guid.NewGuid():N}.json");
        // Unique export: no overwrite and no private account/path information in the payload.
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(settings), ct);
        Status = path;
    });
}

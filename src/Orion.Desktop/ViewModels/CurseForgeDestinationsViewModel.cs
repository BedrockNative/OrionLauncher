using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orion.Desktop.I18n;
using Orion.Domain;
using Orion.Infrastructure.Content;

namespace Orion.Desktop.ViewModels;

public sealed record CurseForgeDestination(bool Shared, IReadOnlyList<ContentTarget> Targets);

/// <summary>Captures installation intent before downloading; worlds are always independent copies.</summary>
public partial class CurseForgeDestinationsViewModel(ContentLibraryService library, InstanceContentService local,
    Func<Task<IReadOnlyList<GameInstance>>> instances, Localizer text) : ObservableObject
{
    public Localizer Text => text;
    public ObservableCollection<ContentTargetViewModel> Targets { get; } = [];
    public string[] Modes => [Text["CfSharedLibrary"], Text["CfSingleInstance"]];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsShared))] private int modeIndex;
    [ObservableProperty] private ContentTargetViewModel? target;
    public bool IsShared => ModeIndex == 0;
    public bool NoInstances => Targets.Count == 0;
    public void RefreshLabels() => OnPropertyChanged(nameof(Modes));
    public async Task RefreshAsync(CancellationToken ct)
    {
        var previous = Targets.ToDictionary(t => t.Instance.Id, t => t.Profile?.Id);
        var selected = Target?.Instance.Id;
        var next = new List<ContentTargetViewModel>();
        foreach (var instance in await instances())
        {
            ct.ThrowIfCancellationRequested();
            var row = new ContentTargetViewModel(instance);
            try
            {
                var snapshot = await local.ListAsync(instance.Id, ct);
                foreach (var p in snapshot.Profiles) row.Profiles.Add(p);
                row.Profile = row.Profiles.FirstOrDefault(p => p.Id == previous.GetValueOrDefault(instance.Id))
                    ?? (row.Profiles.Count == 1 ? row.Profiles[0] : null);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { row.Warning = ex.Message; }
            next.Add(row);
        }
        ct.ThrowIfCancellationRequested();
        Targets.Clear(); foreach (var row in next) Targets.Add(row);
        Target = Targets.FirstOrDefault(t => t.Instance.Id == selected) ?? Targets.FirstOrDefault();
        OnPropertyChanged(nameof(NoInstances));
    }
    public CurseForgeDestination Capture(bool world)
    {
        if (ModeIndex is not (0 or 1)) throw new InvalidOperationException(Text["CfChooseDestination"]);
        var targets = IsShared ? [] : Target is { } row && Targets.Contains(row) ? new[] { row } : [];
        if (!IsShared && targets.Length != 1) throw new InvalidOperationException(Text["CfChooseDestination"]);
        if (targets.Any(t => t.Warning is not null)) throw new InvalidOperationException(Text["CfDestinationUnavailable"]);
        if (world && targets.Any(t => t.Profile is null || !t.Profiles.Contains(t.Profile)))
            throw new InvalidOperationException(Text["ContentChooseProfile"]);
        return new(IsShared, targets.Select(t => new ContentTarget(t.Instance.Id, world ? t.Profile!.Id : null)).ToArray());
    }
    public async Task InstallAsync(string path, CurseForgeDestination destination, CancellationToken ct, string? projectName = null)
    {
        if (destination.Shared ? destination.Targets.Count != 0 : destination.Targets.Count != 1)
            throw new InvalidOperationException(Text["CfChooseDestination"]);
        if (!destination.Shared)
        {
            var target = destination.Targets.Single();
            await local.ImportAsync(target.InstanceId, path, target.ProfileId, ct);
            return;
        }
        await library.ImportAsync(path, ct, projectName);
    }
}

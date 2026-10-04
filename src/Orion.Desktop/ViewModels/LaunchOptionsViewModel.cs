using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orion.Domain;
using Orion.Desktop.I18n;
using Orion.Infrastructure.Runtime;
using System.Collections.ObjectModel;
using Orion.Desktop.Content;
using Orion.Infrastructure.Games;

namespace Orion.Desktop.ViewModels;

public sealed record AccountChoice(string? Id, string Label, XboxProfileViewModel? Profile = null)
{
    public bool IsCurrent => Id is null;
    public bool HasProfile => Profile is not null;
    public bool ShowFallback => Profile is null && !IsCurrent;
    public override string ToString() => Label;
}
public sealed record CoverChoice(string? Id, string Label)
{
    public override string ToString() => Label;
}
public sealed record MonitorResolutionChoice(string Label, GameResolution? Resolution)
{
    public override string ToString() => Label;
}
public sealed record MangoHudPositionChoice(MangoHudPosition Position, string Label)
{
    public override string ToString() => Label;
}

public partial class LaunchOptionsViewModel : ObservableObject
{
    private readonly Func<string?, XboxProfileViewModel?>? profileFor;
    public Guid InstanceId { get; }
    [ObservableProperty] private string name;
    [ObservableProperty] private bool desktopShortcut;
    public InstanceCardViewModel? Card { get; }
    public bool IsExisting => Card is not null;
    public bool CanEdit => Card?.Running != true;
    public void RefreshState() => OnPropertyChanged(nameof(CanEdit));
    public Localizer Text { get; }
    public ObservableCollection<AccountChoice> AccountChoices { get; } = [];
    public IReadOnlyList<CoverChoice> CoverChoices { get; }
    public CoverStore? CoverStore { get; }
    [ObservableProperty] private CoverChoice selectedCover;
    [ObservableProperty] private int selectedTab;
    public Uri? CoverSource => InstanceCovers.Find(SelectedCover?.Id)?.Source;
    public bool HasCover => CoverSource is not null;
    [ObservableProperty] private string? coverLinkError;
    partial void OnSelectedCoverChanged(CoverChoice value)
    { OnPropertyChanged(nameof(CoverSource)); OnPropertyChanged(nameof(HasCover)); CoverLinkError = null; }
    [ObservableProperty] private AccountChoice? selectedAccount;
    [ObservableProperty] private string argumentLines;
    [ObservableProperty] private string launchCommand;
    [ObservableProperty] private string environmentLines;
    [ObservableProperty] private bool customResolution;
    [ObservableProperty] private string width;
    [ObservableProperty] private string height;
    [ObservableProperty] private bool fullscreen;
    [ObservableProperty] private bool showLogOnLaunch;
    [ObservableProperty] private bool mangoHudEnabled;
    [ObservableProperty] private bool mangoHudFps;
    [ObservableProperty] private bool mangoHudFrameTime;
    [ObservableProperty] private bool mangoHudCpu;
    [ObservableProperty] private bool mangoHudGpu;
    [ObservableProperty] private bool mangoHudRam;
    [ObservableProperty] private bool mangoHudVram;
    [ObservableProperty] private bool mangoHudCpuTemperature;
    [ObservableProperty] private bool mangoHudGpuTemperature;
    [ObservableProperty] private bool mangoHudBattery;
    [ObservableProperty] private bool mangoHudResolution;
    [ObservableProperty] private bool mangoHudAvailable;
    [ObservableProperty] private MangoHudPositionChoice selectedMangoHudPosition;
    public IReadOnlyList<MangoHudPositionChoice> MangoHudPositions { get; }
    [RelayCommand] private void RefreshMangoHud() => MangoHudAvailable = MangoHudIntegration.IsAvailable;
    public ObservableCollection<MonitorResolutionChoice> MonitorResolutions { get; } = [];
    [ObservableProperty] private MonitorResolutionChoice? selectedMonitorResolution;
    public bool ResolutionIsLocked => SelectedMonitorResolution?.Resolution is not null;
    public bool CanEditResolution => CustomResolution && !ResolutionIsLocked;
    partial void OnCustomResolutionChanged(bool value) => OnPropertyChanged(nameof(CanEditResolution));
    partial void OnSelectedMonitorResolutionChanged(MonitorResolutionChoice? value)
    {
        if (value?.Resolution is { } size)
        {
            CustomResolution = true;
            Width = size.Width.ToString(CultureInfo.InvariantCulture);
            Height = size.Height.ToString(CultureInfo.InvariantCulture);
        }
        OnPropertyChanged(nameof(ResolutionIsLocked)); OnPropertyChanged(nameof(CanEditResolution));
    }

    public void SetMonitorResolutions(IEnumerable<MonitorResolutionChoice> choices)
    {
        var previous = SelectedMonitorResolution;
        MonitorResolutions.Clear(); MonitorResolutions.Add(new(Text["ResolutionManual"], null));
        foreach (var choice in choices.Where(c => c.Resolution is { Width: >= 320 and <= 16384, Height: >= 200 and <= 16384 }))
            MonitorResolutions.Add(choice);
        if (previous?.Resolution is not null && !MonitorResolutions.Any(c => c.Resolution == previous.Resolution))
            MonitorResolutions.Add(new(Text["ResolutionSaved"] + $" · {previous.Resolution.Width} × {previous.Resolution.Height}", previous.Resolution));
        SelectedMonitorResolution = MonitorResolutions.FirstOrDefault(c => c.Resolution == previous?.Resolution) ?? MonitorResolutions[0];
    }

    public LaunchOptionsViewModel(GameInstance instance, Localizer? text = null, IEnumerable<XboxAccount>? accounts = null, CoverStore? covers = null,
        Func<string?, XboxProfileViewModel?>? profiles = null, InstanceCardViewModel? card = null)
    {
        profileFor = profiles;
        InstanceId = instance.Id; name = instance.Name; desktopShortcut = instance.DesktopShortcut;
        Card = card;
        Text = text ?? new();
        MangoHudPositions = Enum.GetValues<MangoHudPosition>()
            .Select(position => new MangoHudPositionChoice(position, Text["MangoHud" + position])).ToArray();
        selectedMangoHudPosition = MangoHudPositions[0];
        CoverStore = covers;
        CoverChoices = [new(null, Text["CoverMinimal"]), .. InstanceCovers.All.Select(c => new CoverChoice(c.Id, Text[c.LabelKey]))];
        selectedCover = CoverChoices.FirstOrDefault(c => c.Id == instance.CoverId) ?? CoverChoices[0];
        var options = instance.LaunchOptions ?? new();
        options.Validate();
        argumentLines = string.Join('\n', options.Arguments);
        launchCommand = options.LaunchCommand;
        environmentLines = string.Join('\n', options.Environment.Select(pair => $"{pair.Key}={pair.Value}"));
        customResolution = options.Resolution is not null;
        width = (options.Resolution?.Width ?? 1280).ToString(CultureInfo.InvariantCulture);
        height = (options.Resolution?.Height ?? 720).ToString(CultureInfo.InvariantCulture);
        fullscreen = options.Fullscreen;
        showLogOnLaunch = options.ShowLogOnLaunch;
        LoadMangoHud(options.MangoHud ?? new());
        RefreshMangoHud();
        selectedMonitorResolution = options.ResolutionLocked && options.Resolution is { } size
            ? new(Text["ResolutionSaved"], size) : null;
        SetMonitorResolutions([]);
        SetAccounts(accounts ?? [], options.AccountId);
    }

    public void SetAccounts(IEnumerable<XboxAccount> accounts, string? selectedId)
    {
        AccountChoices.Clear(); AccountChoices.Add(new(null, Text["UseCurrentAccount"], profileFor?.Invoke(null)));
        foreach (var account in accounts) AccountChoices.Add(new(account.Id, account.Username, profileFor?.Invoke(account.Id)));
        if (selectedId is not null && AccountChoices.All(a => a.Id != selectedId))
            AccountChoices.Add(new(selectedId, Text["MissingInstanceAccount"]));
        SelectedAccount = AccountChoices.First(a => a.Id == selectedId);
    }

    public InstanceLaunchOptions Build()
    {
        Dictionary<string, string> variables = new(StringComparer.Ordinal);
        foreach (var line in Lines(EnvironmentLines))
        {
            var separator = line.IndexOf('=');
            if (separator < 1) throw new ArgumentException("Use NAME=value, one environment variable per line.");
            var name = line[..separator].Trim();
            if (!variables.TryAdd(name, line[(separator + 1)..]))
                throw new ArgumentException($"Duplicate environment variable: {name}.");
        }
        GameResolution? resolution = null;
        if (CustomResolution && SelectedMonitorResolution?.Resolution is { } monitorSize)
            resolution = monitorSize;
        else if (CustomResolution)
        {
            if (!int.TryParse(Width.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var w)
                || !int.TryParse(Height.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var h))
                throw new ArgumentException("Resolution width and height must be whole numbers.");
            resolution = new(w, h);
        }
        var options = new InstanceLaunchOptions
        {
            Arguments = Lines(ArgumentLines), Environment = variables,
            LaunchCommand = LaunchCommand,
            AccountId = SelectedAccount?.Id,
            Resolution = resolution, ResolutionLocked = CustomResolution && ResolutionIsLocked,
            Fullscreen = CustomResolution && Fullscreen, ShowLogOnLaunch = ShowLogOnLaunch,
            MangoHud = new()
            {
                Enabled = MangoHudEnabled, Fps = MangoHudFps, FrameTime = MangoHudFrameTime,
                Cpu = MangoHudCpu, Gpu = MangoHudGpu, Ram = MangoHudRam, Vram = MangoHudVram,
                CpuTemperature = MangoHudCpuTemperature, GpuTemperature = MangoHudGpuTemperature,
                Battery = MangoHudBattery, Resolution = MangoHudResolution,
                Position = SelectedMangoHudPosition.Position
            }
        };
        options.Validate();
        return options;
    }

    // Literal lines avoid inventing shell parsing, quoting or variable expansion.
    private static string[] Lines(string value) => value.Replace("\r\n", "\n").Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();

    private void LoadMangoHud(MangoHudOptions hud)
    {
        MangoHudEnabled = hud.Enabled; MangoHudFps = hud.Fps; MangoHudFrameTime = hud.FrameTime;
        MangoHudCpu = hud.Cpu; MangoHudGpu = hud.Gpu; MangoHudRam = hud.Ram; MangoHudVram = hud.Vram;
        MangoHudCpuTemperature = hud.CpuTemperature; MangoHudGpuTemperature = hud.GpuTemperature;
        MangoHudBattery = hud.Battery; MangoHudResolution = hud.Resolution;
        SelectedMangoHudPosition = MangoHudPositions.Single(p => p.Position == hud.Position);
    }

    [RelayCommand] private void Reset()
    {
        ArgumentLines = ""; EnvironmentLines = ""; CustomResolution = false;
        LaunchCommand = "%command%";
        Width = "1280"; Height = "720"; Fullscreen = false;
        SelectedMonitorResolution = MonitorResolutions[0];
        ShowLogOnLaunch = false;
        LoadMangoHud(new());
        DesktopShortcut = false;
        SelectedAccount = AccountChoices[0];
        SelectedCover = CoverChoices[0];
    }
}

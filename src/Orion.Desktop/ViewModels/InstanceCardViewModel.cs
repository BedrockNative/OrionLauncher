using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orion.Domain;

namespace Orion.Desktop.ViewModels;

public partial class InstanceCardViewModel : ObservableObject
{
    public GameInstance Instance { get; }
    public MainViewModel Owner { get; }
    public string Name => Instance.Name;
    public string Version => Instance.Version;
    public string Channel => Instance.Channel.ToUpperInvariant();
    public string Monogram => Name[..1].ToUpperInvariant();
    [ObservableProperty] private bool running;
    public IAsyncRelayCommand PlayCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IAsyncRelayCommand ArchiveCommand { get; }
    public IAsyncRelayCommand FolderCommand { get; }
    public IAsyncRelayCommand ContentFolderCommand { get; }
    public IRelayCommand SettingsCommand { get; }
    public IAsyncRelayCommand LogsCommand { get; }

    public InstanceCardViewModel(GameInstance instance, MainViewModel owner)
    {
        Instance = instance; Owner = owner;
        PlayCommand = new AsyncRelayCommand(() => owner.PlayAsync(this));
        StopCommand = new RelayCommand(() => owner.Stop(instance.Id));
        ArchiveCommand = new AsyncRelayCommand(() => owner.ArchiveAsync(this));
        FolderCommand = new AsyncRelayCommand(() => owner.OpenInstanceFolderAsync(instance.Id));
        ContentFolderCommand = new AsyncRelayCommand(() => owner.OpenInstanceContentFolderAsync(instance.Id));
        SettingsCommand = new RelayCommand(() => owner.EditLaunchOptions(this));
        LogsCommand = new AsyncRelayCommand(() => owner.ShowInstanceLogAsync(this));
    }
}

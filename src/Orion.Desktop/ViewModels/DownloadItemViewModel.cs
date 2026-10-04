using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orion.Desktop.I18n;
using Orion.Infrastructure.Games;

namespace Orion.Desktop.ViewModels;

public partial class DownloadItemViewModel : ObservableObject
{
    public Localizer Text { get; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Version), nameof(Status), nameof(Message), nameof(Progress), nameof(Indeterminate), nameof(CanCancel), nameof(CanRetry))]
    private InstallationJob job;
    public string Name => Job.Instance.Name;
    public string Version => $"{Job.Instance.Version} · {Job.Instance.Channel}";
    public string Status => Text["Download" + Job.State];
    public string Message => Job.Message;
    public double Progress => (Job.Fraction ?? 0) * 100;
    public bool Indeterminate => Job.State == DownloadState.Running && Job.Fraction is null;
    public bool CanCancel => Job.State is DownloadState.Queued or DownloadState.Running or DownloadState.Failed;
    public bool CanRetry => Job.State == DownloadState.Failed;
    public IAsyncRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand LogsCommand { get; }
    public DownloadItemViewModel(InstallationJob job, MainViewModel owner)
    {
        this.job = job; Text = owner.Text;
        Text.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Status));
        CancelCommand = new AsyncRelayCommand(() => owner.CancelDownloadAsync(Job.Id));
        RetryCommand = new AsyncRelayCommand(() => owner.RetryDownloadAsync(Job.Id));
        LogsCommand = new AsyncRelayCommand(() => owner.ShowDownloadLogAsync(Job));
    }
}

using CommunityToolkit.Mvvm.Input;
using Orion.Infrastructure.Runtime;

namespace Orion.Desktop.ViewModels;

public sealed class AccountCardViewModel(XboxAccount account, MainViewModel owner, XboxProfileViewModel profile)
{
    public string Id => account.Id;
    public string Username => account.Username;
    public bool Active => account.Active;
    public XboxProfileViewModel Profile { get; } = profile;
    public Task ProfileLoad { get; } = profile.SetAccountAsync(account, true);
    public MainViewModel Owner => owner;
    public IAsyncRelayCommand SelectCommand { get; } = new AsyncRelayCommand(() => owner.SelectAccountAsync(account));
    public IAsyncRelayCommand RemoveCommand { get; } = new AsyncRelayCommand(() => owner.RemoveAccountAsync(account));
}

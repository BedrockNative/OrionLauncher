using CommunityToolkit.Mvvm.ComponentModel;
using Orion.Infrastructure.Linux;
using Orion.Desktop.I18n;

namespace Orion.Desktop.ViewModels;

public partial class FileManagerChoiceViewModel(FileManagerOption option) : ObservableObject
{
    public string Id => option.Id;
    [ObservableProperty] private string label = option.Name;
    public void Refresh(Localizer text, DesktopFolderLauncher launcher) => Label =
        (Id == "system" ? text["FileManagerSystem"] : option.Name) +
        (launcher.IsAvailable(Id) ? "" : " — " + text["FileManagerNotInstalled"]);
}

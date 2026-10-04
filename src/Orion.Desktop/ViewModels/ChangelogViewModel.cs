using CommunityToolkit.Mvvm.ComponentModel;
using Orion.Desktop.Content;
using Orion.Desktop.I18n;

namespace Orion.Desktop.ViewModels;

public partial class ChangelogViewModel : ObservableObject
{
    public Localizer Text { get; }
    public IReadOnlyList<ChangelogEntry> Entries { get; }
    [ObservableProperty] private ChangelogEntry selectedEntry;
    [ObservableProperty] private IReadOnlyList<ChangelogBlock> blocks;
    public ChangelogViewModel(Localizer text, string language)
    {
        Text = text; Entries = ChangelogCatalog.Read(text, language);
        selectedEntry = Entries[0]; blocks = ChangelogCatalog.Format(selectedEntry.Content);
    }
    partial void OnSelectedEntryChanged(ChangelogEntry value) => Blocks = ChangelogCatalog.Format(value.Content);
}

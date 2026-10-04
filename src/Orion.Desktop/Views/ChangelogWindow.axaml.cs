using Avalonia.Controls;
using Avalonia.Interactivity;
using Orion.Desktop.ViewModels;

namespace Orion.Desktop.Views;

public partial class ChangelogWindow : Window
{
    public ChangelogWindow() => InitializeComponent();
    public ChangelogWindow(ChangelogViewModel model) : this()
    {
        DataContext = model;
        model.PropertyChanged += Changed;
        Closed += (_, _) => model.PropertyChanged -= Changed;
    }
    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChangelogViewModel.SelectedEntry)) NotesScroll.ScrollToHome();
    }
    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();
}

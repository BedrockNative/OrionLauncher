namespace Orion.Desktop.Views;

public interface IWindowDialogs
{
    Task<bool> ConfirmAsync(string title, string text, string confirm, string cancel);
    Task OpenFolderAsync(string path);
    Task<string?> ChooseFolderAsync(string title, string hint, IReadOnlyList<FolderChoice> choices, string confirm, string cancel) => Task.FromResult<string?>(null);
    Task OpenWebLinkAsync(Uri uri) => Task.CompletedTask;
    void CloseLogWindows() { }
    Task ShowChangelogAsync(Orion.Desktop.I18n.Localizer text, string language) => Task.CompletedTask;
}

public sealed record FolderChoice(string Path, string Label)
{
    public override string ToString() => Label;
}

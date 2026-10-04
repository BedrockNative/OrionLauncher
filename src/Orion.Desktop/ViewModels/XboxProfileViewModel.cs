using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Orion.Desktop.I18n;
using Orion.Infrastructure.Runtime;

namespace Orion.Desktop.ViewModels;

/// <summary>One account snapshot; cancellation and identity checks prevent stale photos after switching.</summary>
public partial class XboxProfileViewModel : ObservableObject, IDisposable
{
    private readonly Func<string, CancellationToken, Task<XboxProfile?>> cached;
    private readonly Func<string, CancellationToken, Task<XboxProfile?>> fetch;
    private readonly Func<string?, CancellationToken, Task<Bitmap?>> picture;
    private CancellationTokenSource? loading;
    private XboxAccount? account;
    private bool known;
    private bool disposed;
    public Localizer Text { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayName), nameof(Initial), nameof(HasGamertag))] private string? gamertag;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAvatar))] private Bitmap? avatar;
    public bool HasAvatar => Avatar is not null;
    public bool HasAccount => account is not null;
    public bool HasGamertag => !string.IsNullOrEmpty(Gamertag);
    public string Username => account?.Username ?? "";
    public string DisplayName => Gamertag ?? account?.Username ?? Text[known ? "ProfileSignedOut" : "ProfileUnknown"];
    public string Initial => HasAccount ? System.Globalization.StringInfo.GetNextTextElement(DisplayName).ToUpperInvariant() : "?";
    public string Caption => Text[HasAccount ? "LauncherProfile" : "Account"];
    public string Hint => HasAccount ? $"{Text["LauncherProfile"]}: {DisplayName}\n{Text["ManageProfiles"]}" : Text["ManageProfiles"];

    public XboxProfileViewModel(Localizer text,
        Func<string, CancellationToken, Task<XboxProfile?>> cached,
        Func<string, CancellationToken, Task<XboxProfile?>> fetch,
        Func<string?, CancellationToken, Task<Bitmap?>> picture)
    {
        Text = text; this.cached = cached; this.fetch = fetch; this.picture = picture;
        Text.PropertyChanged += LanguageChanged;
    }
    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => NotifyIdentity();
    partial void OnGamertagChanged(string? value) => OnPropertyChanged(nameof(Hint));
    private void NotifyIdentity()
    {
        foreach (var name in new[] { nameof(DisplayName), nameof(Initial), nameof(Caption), nameof(Hint), nameof(HasAccount), nameof(Username) }) OnPropertyChanged(name);
    }
    public async Task SetAccountAsync(XboxAccount? value, bool accountsKnown)
    {
        if (disposed) return;
        loading?.Cancel(); loading?.Dispose();
        loading = new(); var ct = loading.Token;
        account = value; known = accountsKnown; Gamertag = null; ReplaceAvatar(null); NotifyIdentity();
        if (value is null) return;
        string? loadedUrl = null;
        async Task ApplyAsync(XboxProfile? profile)
        {
            if (profile?.IsValidFor(value.Id) != true || ct.IsCancellationRequested) return;
            Gamertag = profile.Gamertag;
            if (profile.PictureUrl == loadedUrl) return;
            ReplaceAvatar(null);
            var bitmap = await picture(profile.PictureUrl, ct);
            if (ct.IsCancellationRequested) bitmap?.Dispose();
            else { ReplaceAvatar(bitmap); loadedUrl = profile.PictureUrl; }
        }
        // Failures never prevent navigation, sign-in, downloads or game launch.
        try { await ApplyAsync(await cached(value.Id, ct)); }
        catch (Exception) { /* The cache and picture are optional presentation data. */ }
        if (ct.IsCancellationRequested) return;
        try { await ApplyAsync(await fetch(value.Id, ct)); }
        catch (Exception) { /* Keep the last verified profile, or the account/initial fallback. */ }
    }
    private void ReplaceAvatar(Bitmap? next) { var previous = Avatar; Avatar = next; previous?.Dispose(); }
    public void Dispose()
    {
        disposed = true; loading?.Cancel(); loading?.Dispose(); loading = null;
        ReplaceAvatar(null); Text.PropertyChanged -= LanguageChanged;
    }
}

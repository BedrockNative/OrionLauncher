using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Orion.Desktop.Content;

namespace Orion.Desktop.Views;

/// <summary>Decorative project art: cancel/release on recycling, page changes and window close.</summary>
public partial class ProjectCoverView : UserControl
{
    public static readonly StyledProperty<string?> SourceUrlProperty = AvaloniaProperty.Register<ProjectCoverView, string?>(nameof(SourceUrl));
    public static readonly StyledProperty<CoverStore?> StoreProperty = AvaloniaProperty.Register<ProjectCoverView, CoverStore?>(nameof(Store));
    public static readonly StyledProperty<bool> RtxSourceProperty = AvaloniaProperty.Register<ProjectCoverView, bool>(nameof(RtxSource));
    public bool RtxSource { get => GetValue(RtxSourceProperty); set => SetValue(RtxSourceProperty, value); }
    public string? SourceUrl { get => GetValue(SourceUrlProperty); set => SetValue(SourceUrlProperty, value); }
    public CoverStore? Store { get => GetValue(StoreProperty); set => SetValue(StoreProperty, value); }
    private CoverStore.Lease? lease;
    private CancellationTokenSource? loading;
    private bool attached;
    private Visual[] ancestors = [];
    public ProjectCoverView() => InitializeComponent();
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e); attached = true;
        ancestors = this.GetVisualAncestors().ToArray();
        foreach (var ancestor in ancestors) ancestor.PropertyChanged += AncestorChanged;
        Reload();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        foreach (var ancestor in ancestors) ancestor.PropertyChanged -= AncestorChanged;
        ancestors = []; Release(); base.OnDetachedFromVisualTree(e);
    }
    private void AncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    { if (e.Property == IsVisibleProperty) Reload(); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (attached && (change.Property == SourceUrlProperty || change.Property == StoreProperty || change.Property == IsVisibleProperty || change.Property == RtxSourceProperty)) Reload();
    }
    private void Release()
    {
        loading?.Cancel(); loading?.Dispose(); loading = null;
        Picture.Source = null; lease?.Dispose(); lease = null; Fallback.IsVisible = true;
    }
    private void Reload()
    {
        Release();
        if (!attached || !IsEffectivelyVisible || Store is null || SourceUrl is null) return;
        loading = new(); _ = LoadAsync(Store, SourceUrl, loading.Token);
    }
    private async Task LoadAsync(CoverStore store, string url, CancellationToken ct)
    {
        try
        {
            var result = RtxSource ? await store.AcquireRtxAsync(url, ct) : await store.AcquireCurseForgeAsync(url, ct);
            if (ct.IsCancellationRequested) { result?.Dispose(); return; }
            lease = result; Picture.Source = result?.Bitmap; Fallback.IsVisible = result is null;
        }
        catch (Exception) { /* Optional art must not break browsing or installation. */ }
    }
}

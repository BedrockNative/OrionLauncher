using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Orion.Desktop.Content;
using Orion.Desktop.I18n;

namespace Orion.Desktop.Views;

public partial class InstanceCoverView : UserControl
{
    public static readonly StyledProperty<string?> CoverIdProperty = AvaloniaProperty.Register<InstanceCoverView, string?>(nameof(CoverId));
    public static readonly StyledProperty<CoverStore?> StoreProperty = AvaloniaProperty.Register<InstanceCoverView, CoverStore?>(nameof(Store));
    public static readonly StyledProperty<string> InitialProperty = AvaloniaProperty.Register<InstanceCoverView, string>(nameof(Initial), "✧");
    public static readonly StyledProperty<string> ChannelProperty = AvaloniaProperty.Register<InstanceCoverView, string>(nameof(Channel), "RELEASE");
    public static readonly StyledProperty<Localizer?> TextProperty = AvaloniaProperty.Register<InstanceCoverView, Localizer?>(nameof(Text));
    public string? CoverId { get => GetValue(CoverIdProperty); set => SetValue(CoverIdProperty, value); }
    public CoverStore? Store { get => GetValue(StoreProperty); set => SetValue(StoreProperty, value); }
    public string Initial { get => GetValue(InitialProperty); set => SetValue(InitialProperty, value); }
    public string Channel { get => GetValue(ChannelProperty); set => SetValue(ChannelProperty, value); }
    public Localizer? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    private CoverStore.Lease? lease;
    private CancellationTokenSource? loading;
    private bool attached;
    private Visual[] ancestors = [];
    public InstanceCoverView() => InitializeComponent();

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
        ancestors = [];
        Release(); base.OnDetachedFromVisualTree(e);
    }
    private void AncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    { if (e.Property == IsVisibleProperty) Reload(); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (attached && (change.Property == CoverIdProperty || change.Property == StoreProperty || change.Property == IsVisibleProperty))
            Reload();
    }
    private void Release()
    {
        loading?.Cancel(); loading?.Dispose(); loading = null;
        Picture.Source = null; lease?.Dispose(); lease = null;
        MonogramPanel.IsVisible = true; SetStatus(""); ChannelBadge.Background = Brushes.Transparent;
        ChannelText.ClearValue(TextBlock.ForegroundProperty);
        ToolTip.SetTip(this, null);
    }
    private void Reload()
    {
        Release();
        if (!attached || !IsEffectivelyVisible || Store is null || CoverId is null) return;
        loading = new();
        _ = LoadAsync(Store, CoverId, loading.Token);
    }
    private void SetStatus(string value) { LoadStatus.Text = value; LoadStatus.IsVisible = value.Length != 0; }
    private async Task LoadAsync(CoverStore store, string id, CancellationToken ct)
    {
        SetStatus(Text?["CoverLoading"] ?? "Loading…");
        try
        {
            var result = await store.AcquireAsync(id, ct);
            if (ct.IsCancellationRequested) { result?.Dispose(); return; }
            lease = result;
            Picture.Source = result?.Bitmap;
            MonogramPanel.IsVisible = result is null;
            SetStatus(result is null ? Text?["CoverUnavailable"] ?? "Image unavailable" : "");
            ChannelBadge.Background = result is null ? Brushes.Transparent : Brush.Parse("#A010151E");
            if (result is not null) ChannelText.Foreground = Brushes.White;
            else ChannelText.ClearValue(TextBlock.ForegroundProperty);
            if (result is not null)
                ToolTip.SetTip(this, $"Xbox México · CC BY 3.0\n{InstanceCovers.Find(id)!.Source}\nhttps://creativecommons.org/licenses/by/3.0/\n{Text?["CoverChanges"] ?? "Resized and cropped"}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!ct.IsCancellationRequested) SetStatus(Text?["CoverUnavailable"] ?? "Image unavailable");
        }
    }
}

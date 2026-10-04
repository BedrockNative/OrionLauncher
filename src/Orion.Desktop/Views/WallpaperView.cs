using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Orion.Desktop.Theming;
using Orion.Desktop.ViewModels;
using SkiaSharp;

namespace Orion.Desktop.Views;

/// <summary>Bounded local image loading. Only one decoded image is retained, never a directory-sized cache.</summary>
public sealed class WallpaperView : Control
{
    private Bitmap? bitmap;
    private string directory = "";
    private string selectedFile = "";
    private readonly DispatcherTimer rotation = new();
    private string currentPath = "";
    private string[] files = [];
    private CancellationTokenSource? pending;
    private (string Path, double Blur, double Brightness) loaded;
    private AdvancedAppearanceViewModel? model;
    private bool attached;
    private static WallpaperView? source;
    private static event Action? SourceChanged;
    public bool IsMirror { get; init; }
    public WallpaperView()
    {
        IsHitTestVisible = false; ClipToBounds = true;
        rotation.Tick += (_, _) => { if (pending is null && IsEffectivelyVisible) Next(); };
    }
    public void Bind(AdvancedAppearanceViewModel value)
    {
        if (model is not null) model.NextWallpaperRequested -= Next;
        model = value; model.NextWallpaperRequested += Next;
        Refresh();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e); attached = true;
        if (IsMirror) { SourceChanged += InvalidateVisual; InvalidateVisual(); return; }
        source = this;
        if (model is not null) { model.NextWallpaperRequested -= Next; model.NextWallpaperRequested += Next; }
        ThemeManager.Changed += Refresh; ActualThemeVariantChanged += ThemeChanged;
        Refresh();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        rotation.Stop();
        if (IsMirror) { SourceChanged -= InvalidateVisual; base.OnDetachedFromVisualTree(e); return; }
        ThemeManager.Changed -= Refresh; ActualThemeVariantChanged -= ThemeChanged;
        if (model is not null) model.NextWallpaperRequested -= Next;
        pending?.Cancel(); bitmap?.Dispose(); bitmap = null; loaded = default;
        if (ReferenceEquals(source, this)) { source = null; SourceChanged?.Invoke(); }
        base.OnDetachedFromVisualTree(e);
    }
    private void ThemeChanged(object? sender, EventArgs e) => Refresh();
    private void Next()
    {
        if (ThemeManager.Current.WallpaperFile.Length > 0) return;
        if (files.Length == 0) { directory = ""; Refresh(); return; }
        var index = Array.IndexOf(files, currentPath);
        var offset = ThemeManager.Current.RandomWallpaper && files.Length > 1 ? Random.Shared.Next(1, files.Length) : 1;
        currentPath = files[(Math.Max(-1, index) + offset) % files.Length];
        Refresh();
    }
    private async void Refresh()
    {
        if (!attached) return;
        var settings = ThemeManager.Current;
        var profile = ActualThemeVariant == ThemeVariant.Light ? settings.Light : settings.Dark;
        var folderChanged = directory != settings.WallpaperDirectory || selectedFile != settings.WallpaperFile;
        UpdateRotation(settings, folderChanged ? 0 : files.Length);
        // Diffusion is applied to the wallpaper layer, never to foreground text or icons.
        var blur = Math.Sqrt(settings.WallpaperBlur * settings.WallpaperBlur + profile.SurfaceBlur * profile.SurfaceBlur);
        var key = (currentPath, blur, settings.WallpaperBrightness);
        InvalidateVisual(); SourceChanged?.Invoke();
        // A -> B -> A must cancel B even when A is already cached.
        pending?.Cancel();
        if (!folderChanged && loaded == key) return;
        using var cancellation = new CancellationTokenSource(); pending = cancellation;
        try
        {
            await Task.Delay(100, cancellation.Token); // Coalesce slider drags and discard stale folder loads.
            if (folderChanged)
            {
                var requestedDirectory = settings.WallpaperDirectory;
                var requestedFile = settings.WallpaperFile;
                var found = await Task.Run(() => requestedFile.Length == 0 ? Scan(requestedDirectory) : ScanFile(requestedFile), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                directory = requestedDirectory; selectedFile = requestedFile; files = found;
                currentPath = files.Length == 0 ? "" : files[settings.RandomWallpaper ? Random.Shared.Next(files.Length) : 0];
                UpdateRotation(settings, files.Length);
            }
            key = (currentPath, blur, settings.WallpaperBrightness);
            Bitmap? next = null;
            try
            {
                if (currentPath.Length > 0)
                    next = await Task.Run(() => Decode(key.currentPath, blur, settings.WallpaperBrightness), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                var old = bitmap; bitmap = next; next = null; old?.Dispose(); loaded = key;
                if (model is not null) model.WallpaperStatus = files.Length == 0 ? model.Text["WallpaperEmpty"] : selectedFile.Length > 0 ? Path.GetFileName(currentPath) : $"{Path.GetFileName(currentPath)} · {files.Length}";
                InvalidateVisual(); SourceChanged?.Invoke();
            }
            finally { next?.Dispose(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (cancellation.IsCancellationRequested) return;
            bitmap?.Dispose(); bitmap = null; loaded = default; InvalidateVisual(); SourceChanged?.Invoke();
            if (model is not null) model.WallpaperStatus = model.Text["WallpaperFailed"];
        }
        finally { if (ReferenceEquals(pending, cancellation)) pending = null; }
    }
    private void UpdateRotation(Orion.Infrastructure.Storage.AppearanceSettings settings, int count)
    {
        var interval = TimeSpan.FromMinutes(settings.WallpaperIntervalMinutes);
        if (rotation.Interval != interval) rotation.Interval = interval;
        rotation.IsEnabled = attached && !IsMirror && settings.RotateWallpapers && settings.WallpaperFile.Length == 0
            && settings.WallpaperDirectory.Length > 0 && count > 1;
    }
    private static string[] ScanFile(string path)
    {
        if (!IsSupported(path)) throw new IOException("Unsupported wallpaper file.");
        return [path];
    }
    private static bool IsSupported(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif"
        && new FileInfo(path) is { LinkTarget: null, Length: > 0 and <= 32 * 1024 * 1024 };
    public static string[] Scan(string directory)
    {
        if (string.IsNullOrEmpty(directory)) return [];
        return Directory.EnumerateFiles(directory).Take(2000).Where(IsSupported)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static Bitmap Decode(string path, double blur, double brightness)
    {
        if (new FileInfo(path) is not { LinkTarget: null, Length: > 0 and <= 32 * 1024 * 1024 }) throw new IOException();
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream) ?? throw new IOException("Unsupported image.");
        if (codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 40_000_000) throw new IOException("Image too large.");
        var scale = Math.Min(1d, 1920d / Math.Max(codec.Info.Width, codec.Info.Height));
        var size = codec.GetScaledDimensions((float)scale);
        using var decoded = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success) throw new IOException("Incomplete image.");
        var width = Math.Max(1, (int)(codec.Info.Width * scale)); var height = Math.Max(1, (int)(codec.Info.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(width, height)) ?? throw new IOException();
        using var filter = SKImageFilter.CreateBlur((float)(blur / 2), (float)(blur / 2), SKShaderTileMode.Clamp);
        var factor = (float)(brightness / 100);
        using var color = SKColorFilter.CreateColorMatrix([factor,0,0,0,0, 0,factor,0,0,0, 0,0,factor,0,0, 0,0,0,1,0]);
        using var paint = new SKPaint { ImageFilter = filter, ColorFilter = color, FilterQuality = SKFilterQuality.Medium };
        surface.Canvas.DrawBitmap(decoded, new SKRect(0, 0, width, height), paint);
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var encoded = data.AsStream(); return new Bitmap(encoded);
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (IsMirror) { source?.DrawWallpaper(context, Bounds.Size); return; }
        DrawWallpaper(context, Bounds.Size);
    }
    private void DrawWallpaper(DrawingContext context, Size size)
    {
        if (bitmap is null) return;
        var settings = ThemeManager.Current;
        var profile = ActualThemeVariant == ThemeVariant.Light ? settings.Light : settings.Dark;
        var brush = new ImageBrush(bitmap)
        {
            Stretch = settings.WallpaperFit switch { "fit" => Stretch.Uniform, "stretch" => Stretch.Fill, "center" or "tile" or "top_left" or "top_right" => Stretch.None, _ => Stretch.UniformToFill },
            AlignmentX = settings.WallpaperFit switch { "top_left" => AlignmentX.Left, "top_right" => AlignmentX.Right, _ => AlignmentX.Center },
            AlignmentY = settings.WallpaperFit is "top_left" or "top_right" ? AlignmentY.Top : AlignmentY.Center,
            Opacity = settings.WallpaperOpacity / 100,
            TileMode = settings.WallpaperFit == "tile" ? TileMode.Tile : TileMode.None
        };
        if (settings.WallpaperFit == "tile") brush.DestinationRect = new RelativeRect(new Rect(bitmap.Size), RelativeUnit.Absolute);
        context.FillRectangle(brush, new Rect(size));
        var overlay = ActualThemeVariant == ThemeVariant.Light ? Colors.White : Colors.Black;
        // Headings and navigation outside panels must remain readable too, including on white/black photos.
        var veil = profile.ProtectReadability ? Math.Max(.88, profile.OverlayOpacity / 100) : profile.OverlayOpacity / 100;
        context.FillRectangle(new SolidColorBrush(overlay, veil), new Rect(size));
    }
}

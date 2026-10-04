using Avalonia.Media.Imaging;

namespace Orion.Desktop.Content;

/// <summary>Disk thumbnails and reference-counted bitmaps. No preload, timers or unbounded RAM cache.</summary>
public sealed class CoverStore(HttpClient http, string directory) : IDisposable
{
    public const int ThumbnailWidth = 320;
    public const int MaximumDownloadBytes = 2 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Entry> images = [];
    private bool disposed;
    public int LoadedCount { get { lock (images) return images.Count; } }

    public Task<Lease?> AcquireAsync(string? id, CancellationToken ct)
    {
        return InstanceCovers.Find(id) is { } cover ? AcquireCoreAsync(cover.Id, cover.Image, ct) : Task.FromResult<Lease?>(null);
    }

    public Task<Lease?> AcquireCurseForgeAsync(string? url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !Orion.Infrastructure.CurseForge.CurseForgeClient.IsDownloadUri(uri))
            return Task.FromResult<Lease?>(null);
        var id = "cf-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        return AcquireCoreAsync(id, uri, ct);
    }

    public Task<Lease?> AcquireRtxAsync(string? url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !Orion.Infrastructure.Rtx.RtxCatalog.IsAssetUri(uri))
            return Task.FromResult<Lease?>(null);
        var id = "rtx-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        return AcquireCoreAsync(id, uri, ct);
    }

    private async Task<Lease?> AcquireCoreAsync(string id, Uri image, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (images)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (images.TryGetValue(id, out var existing))
                { existing.References++; return new(this, id, existing.Bitmap); }
            }
            var bitmap = await Task.Run(() => LoadAsync(id, image, ct), ct).ConfigureAwait(false);
            lock (images)
            {
                if (disposed || ct.IsCancellationRequested)
                { bitmap.Dispose(); ct.ThrowIfCancellationRequested(); throw new ObjectDisposedException(nameof(CoverStore)); }
                images.Add(id, new(bitmap));
            }
            return new(this, id, bitmap);
        }
        finally { gate.Release(); }
    }

    private async Task<Bitmap> LoadAsync(string id, Uri image, CancellationToken ct)
    {
        var path = Path.Combine(directory, id + "-320.png");
        if (File.Exists(path))
        {
            try
            {
                if (new FileInfo(path).Length > MaximumDownloadBytes) throw new InvalidDataException("Oversized cached cover.");
                using var cached = File.OpenRead(path);
                return Decode(cached);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
            { File.Delete(path); } // Only this known, disposable thumbnail; never game data.
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, image);
        request.Headers.UserAgent.ParseAdd("OrionLauncher/0.6 (+https://github.com/BedrockNative/OrionLauncher)");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType is not ("image/jpeg" or "image/png" or "image/webp")
            || response.Content.Headers.ContentLength > MaximumDownloadBytes)
            throw new InvalidDataException("Unsupported cover response.");
        using var bytes = new MemoryStream();
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[16384];
        int count;
        while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > MaximumDownloadBytes) throw new InvalidDataException("Cover is too large.");
            bytes.Write(buffer, 0, count);
        }
        bytes.Position = 0;
        var bitmap = Decode(bytes);
        var temporary = path + ".part";
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            bitmap.Save(temporary);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            if (id.StartsWith("cf-", StringComparison.Ordinal) || id.StartsWith("rtx-", StringComparison.Ordinal)) TrimProjectCache();
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void TrimProjectCache()
    {
        // Only disposable project thumbnails, never covers or user files. RAM is lease-bound.
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*-320.png")
                .Where(f => f.Name.StartsWith("cf-", StringComparison.Ordinal) && f.Name.Length == 75 || f.Name.StartsWith("rtx-", StringComparison.Ordinal) && f.Name.Length == 76)
                .OrderByDescending(f => f.LastWriteTimeUtc).Skip(256)) file.Delete();
        }
        catch (IOException) { /* A read-only cache must not break a decoded cover. */ }
        catch (UnauthorizedAccessException) { }
    }

    private static Bitmap Decode(Stream stream)
    {
        var position = stream.Position;
        // Inspect dimensions before allocating pixels, including for cached files.
        // The existing Avalonia Skia backend supplies this decoder.
        using (var managed = new SkiaSharp.SKManagedStream(stream, disposeManagedStream: false))
        using (var codec = SkiaSharp.SKCodec.Create(managed))
        {
            if (codec is null || codec.Info.Width is < 1 or > 4096 || codec.Info.Height is < 1 or > 4096
                || codec.Info.Height > codec.Info.Width || (long)codec.Info.Width * codec.Info.Height > 4 * 1024 * 1024)
                throw new InvalidDataException("Invalid or oversized landscape cover.");
        }
        stream.Position = position;
        var bitmap = Bitmap.DecodeToWidth(stream, ThumbnailWidth);
        if (bitmap.PixelSize.Height is > 320 or < 1)
        { bitmap.Dispose(); throw new InvalidDataException("Cover must be a landscape image."); }
        return bitmap;
    }
    private void Release(string id, Bitmap bitmap)
    {
        lock (images)
        {
            if (!images.TryGetValue(id, out var entry) || !ReferenceEquals(entry.Bitmap, bitmap)) return;
            if (--entry.References != 0) return;
            images.Remove(id); bitmap.Dispose();
        }
    }
    public void Dispose()
    {
        lock (images)
        {
            disposed = true;
            foreach (var entry in images.Values) entry.Bitmap.Dispose();
            images.Clear();
        }
    }
    private sealed class Entry(Bitmap bitmap)
    {
        public Bitmap Bitmap { get; } = bitmap;
        public int References { get; set; } = 1;
    }
    public sealed class Lease(CoverStore owner, string id, Bitmap bitmap) : IDisposable
    {
        private bool disposed;
        public Bitmap Bitmap { get; } = bitmap;
        public void Dispose() { if (disposed) return; disposed = true; owner.Release(id, Bitmap); }
    }
}

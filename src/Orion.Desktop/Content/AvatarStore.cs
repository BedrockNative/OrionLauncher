using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Orion.Infrastructure.Runtime;

namespace Orion.Desktop.Content;

/// <summary>Bounded 64px avatars with an offline disk cache; each profile shares its bitmap across views.</summary>
public sealed class AvatarStore(HttpClient http, string directory)
{
    public async Task<Bitmap?> LoadAsync(string? url, CancellationToken ct)
    {
        if (url is null || !XboxProfile.IsTrustedPicture(url)) return null;
        return await Task.Run(async () =>
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
            var path = Path.Combine(directory, key + ".png");
            if (File.Exists(path))
            {
                try { using var file = File.OpenRead(path); return Decode(file); }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
                { File.Delete(path); }
            }
            using var response = await http.GetAsync(ThumbnailUri(url), HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType is not ("image/png" or "image/jpeg")
                || response.Content.Headers.ContentLength > 2 * 1024 * 1024)
                throw new InvalidDataException("Invalid Xbox picture response.");
            using var bytes = new MemoryStream();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(buffer, ct)) != 0)
            {
                if (bytes.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("Xbox picture is too large.");
                bytes.Write(buffer, 0, count);
            }
            bytes.Position = 0;
            var bitmap = Decode(bytes);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(directory);
                bitmap.Save(temporary);
                ct.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, ct);
    }

    private static Uri ThumbnailUri(string url)
    {
        var uri = new Uri(url);
        // Xbox's image proxy supports server-side resizing; avoid downloading multi-MB originals.
        if (uri.Host != "images-eds-ssl.xboxlive.com" || uri.AbsolutePath != "/image") return uri;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => Uri.UnescapeDataString(part.Split('=', 2)[0]).ToLowerInvariant() is not ("w" or "h"));
        return new UriBuilder(uri) { Query = string.Join("&", query.Append("w=64").Append("h=64")), Fragment = "" }.Uri;
    }

    private static Bitmap Decode(Stream stream)
    {
        var position = stream.Position;
        using (var managed = new SkiaSharp.SKManagedStream(stream, disposeManagedStream: false))
        using (var codec = SkiaSharp.SKCodec.Create(managed))
        {
            if (codec is null || codec.Info.Width is < 1 or > 2048 || codec.Info.Height is < 1 or > 2048
                || codec.Info.Height > codec.Info.Width * 2 || codec.Info.Width > codec.Info.Height * 2)
                throw new InvalidDataException("Invalid Xbox picture dimensions.");
        }
        stream.Position = position;
        return Bitmap.DecodeToWidth(stream, 64);
    }
}

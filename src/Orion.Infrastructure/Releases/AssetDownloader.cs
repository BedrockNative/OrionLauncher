using System.Security.Cryptography;
using Orion.Domain;

namespace Orion.Infrastructure.Releases;

public sealed class AssetDownloader(HttpClient http)
{
    public async Task DownloadAsync(ReleaseAsset asset, string destination, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        if (asset.Download.Scheme != "https") throw new InvalidDataException("Downloads require HTTPS.");
        await new ResumableDownload(http).DownloadAsync(asset.Download, destination, progress, ct, asset.Size);
        if (asset.Digest is { } digest)
        {
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsupported release checksum algorithm.");
            await using var file = File.OpenRead(destination);
            var hash = await SHA256.HashDataAsync(file, ct);
            if (!string.Equals(Convert.ToHexString(hash), digest[7..], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Release SHA-256 checksum mismatch.");
        }
    }
}

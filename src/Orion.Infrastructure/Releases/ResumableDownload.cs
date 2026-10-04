using System.Net;
using System.Net.Http.Headers;
using Orion.Domain;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Releases;

/// <summary>Persists encrypted bytes only. A range is reused only with a matching strong validator.</summary>
public sealed class ResumableDownload(HttpClient http)
{
    private sealed record Checkpoint(string Source, string? Validator, long Length);

    public async Task DownloadAsync(Uri source, string path, IProgress<OperationProgress>? progress, CancellationToken ct, long? expectedSize = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var statePath = path + ".json";
        var state = await AtomicFile.ReadJsonAsync<Checkpoint>(statePath, ct);
        long offset = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (state?.Source != source.AbsoluteUri || offset > state.Length || expectedSize is not null && state.Length != expectedSize) { state = null; offset = 0; }
        if (state is not null && state.Length == offset && offset > 0)
        { progress?.Report(new("Package downloaded", 1)); return; }
        if (state?.Validator is null) offset = 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.AcceptEncoding.Add(new("identity"));
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            request.Headers.TryAddWithoutValidation("If-Range", state!.Validator);
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (offset == 0 || range?.Unit != "bytes" || range.From != offset || range.Length != state!.Length
                || range.To != range.Length - 1 || response.Headers.ETag?.ToString() != state.Validator)
                throw new InvalidDataException("Server returned an inconsistent download range; no bytes were appended.");
            total = range.Length!.Value;
        }
        else
        {
            offset = 0;
            total = response.Content.Headers.ContentLength ?? throw new InvalidDataException("Package download requires a declared size.");
        }
        if (total <= 0) throw new InvalidDataException("Empty package download.");
        if (expectedSize is not null && total != expectedSize) throw new InvalidDataException("Download size does not match the release.");
        var validator = response.Headers.ETag is { IsWeak: false } etag ? etag.ToString() : null;
        // Truncate before publishing new metadata, so a crash cannot associate old bytes with a new validator.
        await using var output = new FileStream(path, offset == 0 ? FileMode.Create : FileMode.Open,
            FileAccess.Write, FileShare.Read, 81920, true);
        output.Position = offset;
        await AtomicFile.WriteJsonAsync(statePath, new Checkpoint(source.AbsoluteUri, validator, total), ct);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        byte[] buffer = new byte[81920];
        var nextReport = DateTimeOffset.MinValue;
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (offset + count > total) throw new InvalidDataException("Package exceeds its declared size.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            offset += count;
            if (DateTimeOffset.UtcNow >= nextReport)
            {
                progress?.Report(new($"Downloading package · {offset / 1048576d:F1} / {total / 1048576d:F1} MiB", (double)offset / total));
                nextReport = DateTimeOffset.UtcNow.AddMilliseconds(250);
            }
        }
        await output.FlushAsync(ct);
        if (offset != total) throw new IOException("Download interrupted. The received bytes are saved for retry.");
        progress?.Report(new("Package downloaded", 1));
    }
}

using System.Text.Json;
using Orion.Domain;

namespace Orion.Infrastructure.Rtx;

public sealed partial class RtxCatalog
{
    public async Task<RtxPreset> ResolveLinkAsync(RtxLink link, CancellationToken ct)
    {
        link = RtxLink.Parse(link.ToString());
        if (link.Kind == "creator") return link.CreatorPreset();
        var bytes = await ReadAsync(new($"https://bedrock.graphics/api/preset/{link.Id}"), 256 * 1024, null, ct);
        var preset = Parse("[" + System.Text.Encoding.UTF8.GetString(bytes) + "]").Single();
        if (preset.Id != link.Id) throw new InvalidDataException("The BetterRTX API returned a different preset.");
        return preset;
    }

    public async Task<string> DownloadDlssAsync(string path, CancellationToken ct)
    {
        // Resolve an immutable upstream commit first; do not depend on the deprecated
        // BetterRTX /api/dlss Google Drive links or download arbitrary user-provided URLs.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/NVIDIA/DLSS/commits/main");
        request.Headers.UserAgent.ParseAdd("OrionLauncher/1.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        var metadata = await ReadBoundedAsync(response, 256 * 1024, timeout.Token);
        using var document = JsonDocument.Parse(metadata);
        var sha = document.RootElement.GetProperty("sha").GetString();
        if (sha is not { Length: 40 } || sha.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid NVIDIA commit.");
        var url = $"https://raw.githubusercontent.com/NVIDIA/DLSS/{sha}/lib/Windows_x86_64/rel/nvngx_dlss.dll";
        using var dllResponse = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        dllResponse.EnsureSuccessStatusCode();
        if (dllResponse.RequestMessage?.RequestUri?.AbsoluteUri != url) throw new InvalidDataException("Unexpected NVIDIA download redirect.");
        var dll = await ReadBoundedAsync(dllResponse, 128 * 1024 * 1024, timeout.Token);
        RtxService.ValidateDlss(dll);
        await File.WriteAllBytesAsync(path, dll, timeout.Token);
        return "NVIDIA/DLSS · " + sha;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("RTX response exceeds its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream();
        var buffer = new byte[65536]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("RTX response exceeds its size limit.");
            output.Write(buffer, 0, count);
        }
        if (output.Length == 0 || response.Content.Headers.ContentLength is { } expected && output.Length != expected)
            throw new InvalidDataException("Incomplete RTX download.");
        return output.ToArray();
    }
}

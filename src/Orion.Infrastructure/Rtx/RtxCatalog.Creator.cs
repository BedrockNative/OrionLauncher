using System.Net;
using System.Text;
using System.Text.Json;
using Orion.Application;
using Orion.Domain;

namespace Orion.Infrastructure.Rtx;

public sealed partial class RtxCatalog
{
    private static string CreatorIdentifier(string value) => value.Length is > 0 and <= 100 && char.IsAsciiLetterOrDigit(value[0]) && !value.Contains("..", StringComparison.Ordinal) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
        ? value : throw new InvalidDataException("Invalid BetterRTX version or job identifier.");
    public async Task<IReadOnlyList<RtxCreatorVersion>> CreatorVersionsAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await ReadAsync(new("https://bedrock.graphics/api/build/versions"), 64 * 1024, null, ct));
        var versions = doc.RootElement.GetProperty("versions");
        if (versions.GetArrayLength() is < 1 or > 100) throw new InvalidDataException("Invalid creator versions.");
        return versions.EnumerateArray().Select(v => new RtxCreatorVersion(CreatorIdentifier(v.GetProperty("id").GetString()!),
            v.GetProperty("label").GetString()!, v.TryGetProperty("isDefault", out var d) && d.GetBoolean())).ToArray();
    }
    public async Task<RtxCreatorForm> CreatorFormAsync(string version, CancellationToken ct) => RtxCreatorForm.Parse(Encoding.UTF8.GetString(
        await ReadAsync(new($"https://bedrock.graphics/api/build/versions/{CreatorIdentifier(version)}/form"), 2 * 1024 * 1024, null, ct)));

    public async Task<RtxPreset> BuildCreatorAsync(string version, RtxCreatorForm form, IReadOnlyDictionary<string, JsonElement> values,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        CreatorIdentifier(version);
        var settings = form.SerializeSettings(values);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(15));
        string? job = null; var completed = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://bedrock.graphics/api/build?version={version}")
                { Content = new StringContent(settings, Encoding.UTF8, "application/json") };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("BetterRTX requires browser authentication or anti-bot verification for compilation. Orion cannot bypass it. Catalog installation remains available; you can also import a .rtpack exported by the official creator.");
            response.EnsureSuccessStatusCode();
            using var first = JsonDocument.Parse(await ReadBoundedAsync(response, 128 * 1024, timeout.Token));
            job = CreatorIdentifier(first.RootElement.GetProperty("id").GetString()!);
            var status = first.RootElement.GetProperty("status").GetString();
            while (status != "completed")
            {
                if (status is "failed" or "cancelled") throw new InvalidOperationException("BetterRTX compilation " + status + ". No instance was changed.");
                if (status is not ("pending" or "queued" or "building" or "processing")) throw new InvalidDataException("Unknown BetterRTX build state.");
                progress?.Report(new("BetterRTX · " + status));
                await Task.Delay(TimeSpan.FromSeconds(5), timeout.Token);
                using var poll = JsonDocument.Parse(await ReadAsync(new($"https://bedrock.graphics/api/build/{job}"), 128 * 1024, null, timeout.Token));
                status = poll.RootElement.GetProperty("status").GetString();
            }
            completed = true;
            return new("build-" + job, "BetterRTX " + version + " · Custom", null, version, null,
                new[] { "RTXStub", "RTXPostFX.Bloom", "RTXPostFX.Tonemapping" }.ToDictionary(n => n,
                    n => new Uri($"https://bedrock.graphics/api/build/{job}/file/{n}.material.bin")));
        }
        finally
        {
            if (job is not null && !completed)
            {
                // Cancellation only applies to the job created by this operation, never other users' jobs.
                using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { using var response = await http.PostAsync($"https://bedrock.graphics/api/build/{job}/cancel", null, cancelTimeout.Token); }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
            }
        }
    }
}

using System.Net;
using System.Text.Json;
using Orion.Application;
using Orion.Domain;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Releases;

/// <summary>Repository-independent latest stable release resolver with conditional HTTP requests.</summary>
public sealed class GitHubReleaseClient(HttpClient http, string cacheDirectory) : IReleaseClient
{
    public async Task<Release> GetLatestAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        var cacheFile = Path.Combine(cacheDirectory, $"{repository.Owner}-{repository.Name}.json");
        CachedRelease? cache;
        try { cache = await AtomicFile.ReadJsonAsync<CachedRelease>(cacheFile, cancellationToken); }
        catch (JsonException) { cache = null; }
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("OrionLauncher/0.6");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (cache?.ETag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified && cache is not null) return cache.Release;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException($"GitHub rate limit or access restriction for {repository}. Try again later.", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("GitHub returned a non-stable release.");
        var release = new Release(root.GetProperty("tag_name").GetString()!,
            Https(root.GetProperty("html_url").GetString()!),
            root.GetProperty("assets").EnumerateArray().Select(a => new ReleaseAsset(
                a.GetProperty("id").GetInt64(), a.GetProperty("name").GetString()!,
                Https(a.GetProperty("browser_download_url").GetString()!), a.GetProperty("size").GetInt64(),
                a.TryGetProperty("digest", out var digest) ? digest.GetString() : null)).ToArray());
        await AtomicFile.WriteJsonAsync(cacheFile, new CachedRelease(response.Headers.ETag?.ToString(), release), cancellationToken);
        return release;
    }

    private static Uri Https(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        ? uri : throw new InvalidDataException("Release URLs must use HTTPS.");
    public sealed record CachedRelease(string? ETag, Release Release);
}

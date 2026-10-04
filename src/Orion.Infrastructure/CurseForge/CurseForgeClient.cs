using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Orion.Infrastructure.CurseForge;

/// <summary>Credentials go ONLY to api.curseforge.com. Downloads use a separate, unauthenticated client.</summary>
public sealed class CurseForgeClient : IDisposable
{
    public const int BedrockGameId = 78022;
    public const int AddonsClassId = 4984;
    public const int MapsClassId = 6913;
    public const int TexturesClassId = 6929;
    public const int PageSize = 20;
    private readonly HttpClient api;
    private readonly HttpClient files;
    private readonly Func<string> credential;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public CurseForgeClient() : this(new HttpClientHandler { AllowAutoRedirect = false },
        new HttpClientHandler { AllowAutoRedirect = false }, CurseForgeCredential.Read) { }
    public CurseForgeClient(HttpMessageHandler apiHandler, HttpMessageHandler fileHandler, Func<string> credential)
    {
        api = new(apiHandler) { Timeout = TimeSpan.FromSeconds(30) };
        files = new(fileHandler) { Timeout = TimeSpan.FromMinutes(20) };
        this.credential = credential;
    }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(credential());
    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        var key = credential();
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("CurseForge is unavailable in this build. Use an official build, configure your developer key, or import a local file.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.curseforge.com/v1/" + path);
        request.Headers.Add("x-api-key", key);
        request.Headers.UserAgent.ParseAdd("OrionLauncher/0.6");
        using var response = await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "CurseForge did not authorize this request.",
                HttpStatusCode.TooManyRequests => "CurseForge rate limit reached. Please try again later.",
                _ => $"CurseForge request failed (HTTP {(int)response.StatusCode})."
            }); // Never surface response bodies, headers or credentials in errors.
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("CurseForge response is too large.");
            buffer.Write(chunk, 0, count);
        }
        return JsonSerializer.Deserialize<T>(buffer.ToArray(), Json) ?? throw new InvalidDataException("Invalid CurseForge response.");
    }
    private sealed record Envelope<T>(T Data);
    public static bool IsSupportedCategory(int id) => id is AddonsClassId or MapsClassId or TexturesClassId;
    public async Task<CfCategory[]> CategoriesAsync(CancellationToken ct) =>
        (await GetAsync<Envelope<CfCategory[]>>($"categories?gameId={BedrockGameId}&classesOnly=true", ct)).Data
            .Where(c => c.IsClass && IsSupportedCategory(c.Id)).DistinctBy(c => c.Id).ToArray();
    public Task<CfPage<CfProject>> SearchAsync(string query, int category, int index, CancellationToken ct)
    {
        if (index is < 0 or > 9980) throw new ArgumentOutOfRangeException(nameof(index));
        // Never issue an unfiltered search: it also returns skins and standalone scripts.
        if (!IsSupportedCategory(category)) throw new ArgumentOutOfRangeException(nameof(category), "Choose Bedrock addons, maps or textures.");
        return GetAsync<CfPage<CfProject>>($"mods/search?gameId={BedrockGameId}&classId={category}&searchFilter={Uri.EscapeDataString(query)}&sortField=2&sortOrder=desc&pageSize={PageSize}&index={index}", ct);
    }
    public Task<CfPage<CfFile>> FilesAsync(int projectId, int index, CancellationToken ct) =>
        GetAsync<CfPage<CfFile>>($"mods/{Positive(projectId)}/files?pageSize={PageSize}&index={Math.Clamp(index, 0, 9980)}", ct);
    private static int Positive(int id) => id > 0 ? id : throw new ArgumentOutOfRangeException(nameof(id));
    public static bool IsDownloadUri(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.IdnHost.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase);
    public static bool IsProjectUri(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.IdnHost is "www.curseforge.com" or "curseforge.com";

    /// <summary>Revalidate authoritative metadata and author distribution settings before downloading.</summary>
    public async Task DownloadAsync(int projectId, int fileId, string target, IProgress<double>? progress, CancellationToken ct)
    {
        var project = (await GetAsync<Envelope<CfProject>>($"mods/{Positive(projectId)}", ct)).Data;
        var file = (await GetAsync<Envelope<CfFile>>($"mods/{projectId}/files/{Positive(fileId)}", ct)).Data;
        if (project.Id != projectId || project.GameId != BedrockGameId || file.GameId != BedrockGameId || file.ModId != projectId || file.Id != fileId)
            throw new InvalidDataException("This file does not belong to a Minecraft Bedrock project.");
        if (!file.IsAvailable) throw new InvalidOperationException("This CurseForge file is no longer available.");
        var hash = CurseForgeIntegrity.Sha1(file);
        if (project.AllowModDistribution == false || string.IsNullOrWhiteSpace(file.DownloadUrl))
        {
            if (!Uri.TryCreate(project.Links?.WebsiteUrl, UriKind.Absolute, out var website) || !IsProjectUri(website)
                || !website.AbsolutePath.StartsWith("/minecraft-bedrock/", StringComparison.Ordinal))
                throw new InvalidDataException("CurseForge did not provide a valid project website.");
            var page = new UriBuilder(website) { Path = website.AbsolutePath.TrimEnd('/') + $"/files/{fileId}", Query = "", Fragment = "" }.Uri;
            throw new ManualDownloadRequiredException(file, page);
        }
        var url = new Uri(file.DownloadUrl);
        HttpResponseMessage? response = null;
        try
        {
            for (var hop = 0; hop < 5; hop++)
            {
                if (!IsDownloadUri(url)) throw new InvalidDataException("Untrusted CurseForge download address.");
                response = await files.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var next = response.Headers.Location ?? throw new InvalidDataException("Invalid download redirect.");
                    url = new Uri(url, next); response.Dispose(); response = null; continue;
                }
                break;
            }
            if (response is null || !response.IsSuccessStatusCode) throw new HttpRequestException("CurseForge download failed.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            try
            {
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > file.FileLength) throw new InvalidDataException("Download exceeds its declared size.");
                    digest.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress?.Report((double)total / file.FileLength);
                }
                if (total != file.FileLength || !Convert.ToHexString(digest.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Download integrity check failed.");
            }
            catch { await output.DisposeAsync(); File.Delete(target); throw; }
        }
        finally { response?.Dispose(); }
    }
    public void Dispose() { api.Dispose(); files.Dispose(); }
}

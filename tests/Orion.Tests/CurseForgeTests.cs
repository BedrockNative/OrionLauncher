using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orion.Infrastructure.CurseForge;

namespace Orion.Tests;

public sealed class CurseForgeTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Count++; return Task.FromResult(respond(request)); }
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
    private static CfProject Project(bool allow = true) => new(42, CurseForgeClient.BedrockGameId, "Example", "Summary", [new("Author")], new("https://www.curseforge.com/minecraft-bedrock/addons/example"), allow, 100);
    private static CfFile FileMetadata(byte[] bytes, string? url = "https://edge.forgecdn.net/files/1/test.mcpack") =>
        new(99, 42, CurseForgeClient.BedrockGameId, "Example 1.0", "example.mcpack", bytes.Length, true, url,
            [new(Convert.ToHexString(SHA1.HashData(bytes)), 1)], ["1.21"], []);
    [Fact]
    public async Task MissingCredentialDoesNotMakeNetworkRequests()
    {
        var handler = new Handler(_ => throw new Exception("Network must not be used"));
        using var client = new CurseForgeClient(handler, handler, () => "");
        Assert.False(client.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync("test", CurseForgeClient.AddonsClassId, 0, default));
        Assert.Equal(0, handler.Count);
    }
    [Fact]
    public async Task SearchIsBedrockScopedAndEscapedAndOnlyApiGetsCredential()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("api.curseforge.com", request.RequestUri!.Host);
            Assert.Equal("test-credential", request.Headers.GetValues("x-api-key").Single());
            Assert.Contains("gameId=78022", request.RequestUri.Query);
            Assert.Contains("classId=4984", request.RequestUri.Query);
            Assert.Contains("searchFilter=hello%26gameId%3D432", request.RequestUri.Query);
            return Json(new { data = new[] { Project() }, pagination = new { index = 0, resultCount = 1, totalCount = 1 } });
        });
        using var client = new CurseForgeClient(handler, new Handler(_ => throw new Exception()), () => "test-credential");
        Assert.Single((await client.SearchAsync("hello&gameId=432", CurseForgeClient.AddonsClassId, 0, default)).Data);
    }
    [Fact]
    public async Task DoesNotFollowApiRedirectsOrExposeResponseBody()
    {
        var handler = new Handler(_ => new(HttpStatusCode.Redirect)
        { Headers = { Location = new("https://attacker.invalid/") }, Content = new StringContent("sensitive-response-body") });
        using var client = new CurseForgeClient(handler, handler, () => "test-credential");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.CategoriesAsync(default));
        Assert.DoesNotContain("sensitive", error.Message); Assert.DoesNotContain("test-credential", error.Message); Assert.Equal(1, handler.Count);
    }
    [Fact]
    public async Task DownloadUsesSeparateClientAndVerifiesHash()
    {
        using var dir = new TestDirectory(); var bytes = Encoding.UTF8.GetBytes("package payload");
        var api = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/42") ? Json(new { data = Project() }) : Json(new { data = FileMetadata(bytes) }));
        var cdn = new Handler(request =>
        { Assert.False(request.Headers.Contains("x-api-key")); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; });
        using var client = new CurseForgeClient(api, cdn, () => "test-credential");
        var target = Path.Combine(dir.Root, "pack.mcpack");
        await client.DownloadAsync(42, 99, target, null, default);
        Assert.Equal(bytes, await System.IO.File.ReadAllBytesAsync(target));
    }
    [Theory]
    [InlineData("https://forgecdn.net.attacker.invalid/payload")]
    [InlineData("http://edge.forgecdn.net/payload")]
    [InlineData("https://127.0.0.1/payload")]
    [InlineData("https://user@edge.forgecdn.net/payload")]
    public async Task RejectsUntrustedDownloadUrls(string url)
    {
        using var dir = new TestDirectory(); var bytes = new byte[] { 1 };
        var api = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/42") ? Json(new { data = Project() }) : Json(new { data = FileMetadata(bytes, url) }));
        var cdn = new Handler(_ => throw new Exception("No CDN request expected"));
        using var client = new CurseForgeClient(api, cdn, () => "test");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(42, 99, Path.Combine(dir.Root, "download"), null, default));
        Assert.Equal(0, cdn.Count);
    }
    [Theory]
    [InlineData(false, "https://edge.forgecdn.net/file")]
    [InlineData(true, null)]
    public async Task DistributionRestrictionIsRespected(bool distribution, string? downloadUrl)
    {
        using var dir = new TestDirectory();
        var api = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/42") ? Json(new { data = Project(distribution) }) : Json(new { data = FileMetadata([1], downloadUrl) }));
        var cdn = new Handler(_ => throw new Exception("No CDN request expected"));
        using var client = new CurseForgeClient(api, cdn, () => "test");
        var blocked = await Assert.ThrowsAsync<ManualDownloadRequiredException>(() => client.DownloadAsync(42, 99, Path.Combine(dir.Root, "download"), null, default));
        Assert.Equal("https://www.curseforge.com/minecraft-bedrock/addons/example/files/99", blocked.Page.AbsoluteUri);
        Assert.Equal(99, blocked.File.Id);
        Assert.Equal(0, cdn.Count);
    }
    [Fact]
    public async Task CorruptDownloadIsRemoved()
    {
        using var dir = new TestDirectory();
        var api = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/42") ? Json(new { data = Project() }) : Json(new { data = FileMetadata([1, 2]) }));
        var cdn = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 3]) });
        using var client = new CurseForgeClient(api, cdn, () => "test");
        var target = Path.Combine(dir.Root, "download");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(42, 99, target, null, default));
        Assert.False(System.IO.File.Exists(target));
    }
    [Fact]
    public async Task DownloadRedirectToNonCdnIsRejectedBeforeContact()
    {
        using var dir = new TestDirectory();
        var api = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/42") ? Json(new { data = Project() }) : Json(new { data = FileMetadata([1]) }));
        var cdn = new Handler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new("https://localhost/private") } });
        using var client = new CurseForgeClient(api, cdn, () => "test");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(42, 99, Path.Combine(dir.Root, "download"), null, default));
        Assert.Equal(1, cdn.Count);
    }
    [Fact]
    public async Task ExistingDownloadTargetIsNeverDeletedOrOverwritten()
    {
        using var dir = new TestDirectory();
        var api = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/42") ? Json(new { data = Project() }) : Json(new { data = FileMetadata([1]) }));
        var cdn = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
        using var client = new CurseForgeClient(api, cdn, () => "test");
        var target = Path.Combine(dir.Root, "existing"); await System.IO.File.WriteAllTextAsync(target, "Keep me");
        await Assert.ThrowsAsync<IOException>(() => client.DownloadAsync(42, 99, target, null, default));
        Assert.Equal("Keep me", await System.IO.File.ReadAllTextAsync(target));
    }

    [Theory]
    [InlineData(401, "authorize")]
    [InlineData(403, "authorize")]
    [InlineData(429, "rate limit")]
    [InlineData(500, "HTTP 500")]
    public async Task ApiFailuresAreActionableAndDoNotExposeBodiesOrKeys(int status, string expected)
    {
        var api = new Handler(_ => new((HttpStatusCode)status) { Content = new StringContent("private server response with test-credential") });
        using var client = new CurseForgeClient(api, new Handler(_ => throw new Exception()), () => "test-credential");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync("test", CurseForgeClient.AddonsClassId, 0, default));
        Assert.Contains(expected, error.Message); Assert.DoesNotContain("test-credential", error.Message);
        Assert.DoesNotContain("private server", error.Message); Assert.Equal(1, api.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task TruncatedAndOversizedDownloadsRemoveStaging(int actualSize)
    {
        using var dir = new TestDirectory();
        var api = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/42") ? Json(new { data = Project() }) : Json(new { data = FileMetadata([1, 2]) }));
        var cdn = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[actualSize]) });
        using var client = new CurseForgeClient(api, cdn, () => "test");
        var target = Path.Combine(dir.Root, "partial");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(42, 99, target, null, default));
        Assert.False(System.IO.File.Exists(target));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("game")]
    [InlineData("file")]
    [InlineData("mod")]
    [InlineData("hash")]
    [InlineData("available")]
    public async Task InvalidAuthoritativeMetadataNeverContactsCdn(string invalid)
    {
        using var dir = new TestDirectory();
        var project = invalid == "project" ? Project() with { Id = 43 } : Project();
        var file = invalid switch
        {
            "game" => FileMetadata([1]) with { GameId = 432 },
            "file" => FileMetadata([1]) with { Id = 100 },
            "mod" => FileMetadata([1]) with { ModId = 43 },
            "hash" => FileMetadata([1]) with { Hashes = [] },
            "available" => FileMetadata([1]) with { IsAvailable = false },
            _ => FileMetadata([1])
        };
        var api = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/42") ? Json(new { data = project }) : Json(new { data = file }));
        var cdn = new Handler(_ => throw new Exception("Must not contact CDN"));
        using var client = new CurseForgeClient(api, cdn, () => "test");
        var exception = await Record.ExceptionAsync(() => client.DownloadAsync(42, 99, Path.Combine(dir.Root, "download"), null, default));
        Assert.NotNull(exception);
        Assert.True(exception is InvalidDataException or InvalidOperationException);
        Assert.Equal(0, cdn.Count); Assert.Empty(Directory.GetFiles(dir.Root));
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    { public void Report(double value) => report(value); }

    [Fact]
    public async Task MidDownloadCancellationCleansPartialAndCanRetry()
    {
        using var dir = new TestDirectory(); var bytes = new byte[256 * 1024];
        var api = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/42") ? Json(new { data = Project() }) : Json(new { data = FileMetadata(bytes) }));
        var cdn = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var client = new CurseForgeClient(api, cdn, () => "test");
        using var cancel = new CancellationTokenSource(); var target = Path.Combine(dir.Root, "download");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(42, 99, target, new InlineProgress(_ => cancel.Cancel()), cancel.Token));
        Assert.False(System.IO.File.Exists(target));
        await client.DownloadAsync(42, 99, target, null, default);
        Assert.Equal(bytes.Length, new FileInfo(target).Length);
    }

    [Fact]
    public async Task SafeCdnRedirectsKeepCredentialIsolatedAndLoopsAreBounded()
    {
        using var dir = new TestDirectory();
        var api = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/42") ? Json(new { data = Project() }) : Json(new { data = FileMetadata([1]) }));
        var loop = false;
        var cdn = new Handler(request =>
        {
            Assert.False(request.Headers.Contains("x-api-key"));
            return !loop && request.RequestUri!.Host == "mediafilez.forgecdn.net"
                ? new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) }
                : new(HttpStatusCode.Redirect) { Headers = { Location = new("https://mediafilez.forgecdn.net/target") } };
        });
        using var client = new CurseForgeClient(api, cdn, () => "test");
        await client.DownloadAsync(42, 99, Path.Combine(dir.Root, "good"), null, default);
        Assert.Equal(2, cdn.Count); loop = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadAsync(42, 99, Path.Combine(dir.Root, "loop"), null, default));
        Assert.Equal(7, cdn.Count); Assert.False(System.IO.File.Exists(Path.Combine(dir.Root, "loop")));
    }

    [Fact]
    public async Task MetadataResponsesHaveBoundedSizeAndRejectMalformedJson()
    {
        var large = true;
        var api = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(large ? new string('x', 4 * 1024 * 1024 + 1) : "not json") });
        using var client = new CurseForgeClient(api, new Handler(_ => throw new Exception()), () => "test");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CategoriesAsync(default));
        large = false;
        await Assert.ThrowsAsync<JsonException>(() => client.CategoriesAsync(default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6925)]
    [InlineData(6940)]
    [InlineData(-1)]
    public void UnsupportedAndUnfilteredSearchesNeverReachApi(int category)
    {
        var api = new Handler(_ => throw new Exception("Unexpected request"));
        using var client = new CurseForgeClient(api, new Handler(_ => throw new Exception()), () => "test");
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = client.SearchAsync("test", category, 0, default); });
        Assert.Equal(0, api.Count);
    }

    [Fact]
    public async Task CategoriesExposeOnlySupportedDistinctBedrockClasses()
    {
        var api = new Handler(_ => Json(new { data = new[] {
            new CfCategory(4984, "Addons", true), new(6913, "Maps", true), new(6929, "Textures", true),
            new(6925, "Skins", true), new(6940, "Scripts", true), new(123, "Future", true),
            new(4984, "Duplicate", true), new(6913, "Not a class", false) } }));
        using var client = new CurseForgeClient(api, new Handler(_ => throw new Exception()), () => "test");
        Assert.Equal(new[] { 4984, 6913, 6929 }, (await client.CategoriesAsync(default)).Select(c => c.Id));
    }
}

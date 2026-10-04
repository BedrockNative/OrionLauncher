using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Orion.Domain;
using Orion.Infrastructure.Releases;

namespace Orion.Tests;

public sealed class ReleaseTests
{
    private const string ReleaseJson = """
        {"tag_name":"v2","html_url":"https://github.com/example/tool/releases/tag/v2","draft":false,"prerelease":false,
         "assets":[{"id":42,"name":"tool.tar.gz","browser_download_url":"https://github.com/example/tool/releases/download/v2/tool.tar.gz","size":3,"digest":null}]}
        """;

    [Fact]
    public async Task LatestReleaseRevalidatesAnyRepositoryWithEtag()
    {
        using var directory = new TestDirectory();
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("https://api.github.com/repos/example/tool/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.Contains("OrionLauncher", request.Headers.UserAgent.ToString());
            if (++calls == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson) };
                response.Headers.ETag = new EntityTagHeaderValue("\"revision\"");
                return response;
            }
            Assert.Equal("\"revision\"", request.Headers.IfNoneMatch.Single().Tag);
            return new(HttpStatusCode.NotModified);
        }));
        var client = new GitHubReleaseClient(http, directory.Paths.Cache);
        Assert.Equal("v2", (await client.GetLatestAsync(new("example", "tool"))).Tag);
        Assert.Equal(42, (await client.GetLatestAsync(new("example", "tool"))).Assets.Single().Id);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("../owner", "repo")]
    [InlineData("owner", "../repo")]
    [InlineData("owner", "..")]
    [InlineData("owner", "repo?x=1")]
    public void RejectsInvalidRepository(string owner, string repo) => Assert.Throws<ArgumentException>(() => new Repository(owner, repo));

    [Fact]
    public async Task RateLimitIsNotSilentlyTreatedAsLatest()
    {
        using var directory = new TestDirectory();
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.TooManyRequests)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new GitHubReleaseClient(http, directory.Paths.Cache).GetLatestAsync(new("o", "r")));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
    }

    [Fact]
    public async Task RejectsPrerelease()
    {
        using var directory = new TestDirectory();
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson.Replace("\"prerelease\":false", "\"prerelease\":true")) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubReleaseClient(http, directory.Paths.Cache).GetLatestAsync(new("o", "r")));
    }

    [Theory]
    [InlineData(3, true, true)]
    [InlineData(3, false, false)]
    [InlineData(4, true, false)]
    [InlineData(2, true, false)]
    public async Task DownloadsCheckSizeAndDigest(long size, bool digestMatches, bool succeeds)
    {
        using var directory = new TestDirectory();
        var bytes = Encoding.UTF8.GetBytes("abc");
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var digest = Convert.ToHexString(SHA256.HashData(digestMatches ? bytes : [0]));
        var asset = new ReleaseAsset(1, "runtime.tar.gz", new("https://example.test/runtime"), size, "sha256:" + digest);
        var download = new AssetDownloader(http).DownloadAsync(asset, Path.Combine(directory.Root, "asset"), null, CancellationToken.None);
        if (succeeds) { await download; Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(directory.Root, "asset"))); }
        else await Assert.ThrowsAsync<InvalidDataException>(() => download);
    }

    [Fact]
    public void RuntimeSelectionRejectsAmbiguousAssets()
    {
        var asset = new ReleaseAsset(1, "xodus-v1.tar.gz", new("https://example.test/xodus"), 1, null);
        var release = new Release("v1", new("https://example.test/v1"), [asset, asset with { Id = 2 }]);
        Assert.Throws<InvalidDataException>(() => RuntimeManager.SelectAsset(RuntimeDefinition.Xodus, release));
    }

    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}

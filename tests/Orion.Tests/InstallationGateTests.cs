using System.Net;
using Orion.Application;
using Orion.Domain;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Runtime;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

public sealed class InstallationGateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurchaseGatePrecedesGameBytesAndCommitIncludesManifest(bool purchased)
    {
        using var directory = new TestDirectory();
        var runtime = Path.Combine(directory.Paths.Tools, "xodus", "42"); Directory.CreateDirectory(runtime);
        var script = $$"""
            #!/bin/sh
            case "$1" in
              check-ownership) exit {{(purchased ? 0 : 2)}} ;;
              install-owned)
                case "$3" in file://*) ;; *) exit 8 ;; esac
                mkdir -p "$4"
                printf 'encrypted' > "$4/Minecraft.Windows.exe"
                printf 'metadata' > "$4/.xodus-streaming.msixvc"
                ;;
              *) exit 9 ;;
            esac
            """;
        foreach (var name in new[] { "xodus-cli", "xodus-service" })
        {
            var executable = Path.Combine(runtime, name); await File.WriteAllTextAsync(executable, script);
            File.SetUnixFileMode(executable, (UnixFileMode)0x1ED);
        }
        int downloads = 0;
        using var http = new HttpClient(new ReleaseTests.Handler(_ => { downloads++; return new(HttpStatusCode.OK) { Content = new StringContent("encrypted package") }; }));
        var runtimes = new RuntimeManager(directory.Paths, new FixedRelease(), new(http));
        var service = new FakeService();
        var installer = new GameInstaller(directory.Paths, runtimes, new(), new(directory.Paths), service, new(http));
        var instance = GameInstance.Create("Test", "1", "Release");
        var operation = installer.InstallAsync(instance, "https://example.test/game.msixvc", null, default);
        if (!purchased)
        {
            await Assert.ThrowsAsync<IOException>(() => operation);
            Assert.Equal(0, downloads);
            Assert.False(Directory.Exists(directory.Paths.Instance(instance.Id)));
        }
        else
        {
            await operation; Assert.Equal(1, downloads);
            Assert.Equal(instance.Id, (await new InstanceRepository(directory.Paths).GetAsync(instance.Id)).Id);
            GameLayout.Validate(directory.Paths.Game(instance.Id));
            Assert.False(Directory.Exists(directory.Paths.InstallStage(instance.Id)));
            // Recovery after a crash between commit and queue cleanup is idempotent and offline.
            await installer.InstallAsync(instance, "https://example.test/game.msixvc", null, default);
            Assert.Equal(1, downloads); Assert.Equal(1, service.Starts);
        }
    }

    private sealed class FakeService : IXodusService
    {
        public int Starts { get; private set; }
        public event Action<string>? OutputReceived { add { } remove { } }
        public Task EnsureAsync(RuntimeInstallation installation, CancellationToken ct) { Starts++; return Task.CompletedTask; }
    }
    private sealed class FixedRelease : IReleaseClient
    {
        public Task<Release> GetLatestAsync(Repository repository, CancellationToken cancellationToken = default) => Task.FromResult(
            new Release("0.4.0", new("https://example.test/release"), [new(42, "xodus-test.tar.gz", new("https://example.test/runtime"), 1, null)]));
    }
}

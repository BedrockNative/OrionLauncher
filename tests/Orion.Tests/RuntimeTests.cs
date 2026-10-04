using System.Net;
using Orion.Application;
using Orion.Domain;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Runtime;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public async Task BundledRuntimeWorksOfflineAndDoesNotWriteInstallationState()
    {
        using var directory = new TestDirectory();
        var bundle = Path.Combine(directory.Root, "bundle");
        var root = Path.Combine(bundle, "xodus"); Directory.CreateDirectory(root);
        await AtomicFile.WriteJsonAsync(Path.Combine(root, "version.json"), "0.7.3");
        foreach (var name in RuntimeDefinition.Xodus.Executables)
        {
            var file = Path.Combine(root, name); await File.WriteAllTextAsync(file, "fixture");
            File.SetUnixFileMode(file, (UnixFileMode)0x1ED);
        }
        using var http = new HttpClient(new ReleaseTests.Handler(_ => throw new InvalidOperationException("No network permitted")));
        var manager = new RuntimeManager(directory.Paths, new OfflineRelease(), new(http), bundle);
        var installed = await manager.EnsureAsync(RuntimeDefinition.Xodus, null, default);
        Assert.Equal(root, installed.Directory); Assert.Equal("0.7.3", installed.Tag);
        Assert.Equal(installed, await manager.GetInstalledAsync(RuntimeDefinition.Xodus, default));
        Assert.False(File.Exists(Path.Combine(directory.Paths.Tools, "xodus/current.json")));
        // An explicit update can fall back to the packaged runtime when offline.
        Assert.Equal(installed, await manager.EnsureAsync(RuntimeDefinition.Xodus, null, default, checkForUpdates: true));
        // User-installed updates take precedence over the packaged fallback.
        var updated = installed with { Tag = "0.8.0" };
        await AtomicFile.WriteJsonAsync(Path.Combine(directory.Paths.Tools, "xodus/current.json"), updated);
        Assert.Equal(updated, await manager.EnsureAsync(RuntimeDefinition.Xodus, null, default));
        await AtomicFile.WriteJsonAsync(Path.Combine(directory.Paths.Tools, "xodus/current.json"),
            updated with { Directory = Path.Combine(directory.Root, "missing-update") });
        Assert.Equal(installed, await manager.EnsureAsync(RuntimeDefinition.Xodus, null, default, checkForUpdates: true));
    }

    private sealed class OfflineRelease : IReleaseClient
    {
        public Task<Release> GetLatestAsync(Repository repository, CancellationToken ct) => throw new HttpRequestException("Offline");
    }

    [Fact]
    public async Task FailedUpdateKeepsPreviouslyInstalledRuntime()
    {
        using var directory = new TestDirectory();
        var oldRoot = Path.Combine(directory.Paths.Tools, "xodus", "1");
        Directory.CreateDirectory(oldRoot);
        foreach (var name in RuntimeDefinition.Xodus.Executables)
        {
            var file = Path.Combine(oldRoot, name);
            await File.WriteAllTextAsync(file, "old runtime");
            File.SetUnixFileMode(file, (UnixFileMode)0x1ED);
        }
        var installed = new RuntimeInstallation("xodus", "v1", oldRoot);
        var manifest = Path.Combine(directory.Paths.Tools, "xodus", "current.json");
        await AtomicFile.WriteJsonAsync(manifest, installed);
        using var http = new HttpClient(new ReleaseTests.Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));
        var manager = new RuntimeManager(directory.Paths, new FixedRelease(), new(http));
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.EnsureAsync(RuntimeDefinition.Xodus, null, CancellationToken.None));
        Assert.Equal(installed, await AtomicFile.ReadJsonAsync<RuntimeInstallation>(manifest));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(oldRoot)!, ".install-*"));
        Assert.False(Directory.Exists(Path.Combine(directory.Paths.Tools, "xodus", "2")));
    }

    [Fact]
    public async Task PublishedRuntimeArchivesMatchTheIntegrationContract()
    {
        foreach (var (key, definition, executable) in new[] {
            ("ORION_XODUS_ARCHIVE", RuntimeDefinition.Xodus, "xodus-cli"),
            ("ORION_WINEGDK_ARCHIVE", RuntimeDefinition.WineGdk, "wine") })
        {
            var archive = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrEmpty(archive)) continue;
            using var directory = new TestDirectory();
            var output = Path.Combine(directory.Root, "runtime");
            await TarArchive.ExtractAsync(archive, output, CancellationToken.None);
            foreach (var name in definition.Executables) Assert.True(File.Exists(RuntimeManager.FindExecutable(output, name)));
            await new ProcessRunner().RunAsync(new(RuntimeManager.FindExecutable(output, executable), ["--version"], output),
                Path.Combine(directory.Root, "version.log"), CancellationToken.None);
            Assert.NotEmpty(await File.ReadAllTextAsync(Path.Combine(directory.Root, "version.log")));
            if (definition == RuntimeDefinition.Xodus)
            {
                var runner = new ProcessRunner();
                foreach (var command in new[] { "install-owned", "check-ownership", "run", "accounts" })
                {
                    var log = Path.Combine(directory.Root, command + "-help.log");
                    await runner.RunAsync(new(RuntimeManager.FindExecutable(output, executable), [command, "--help"], output),
                        log, CancellationToken.None);
                    var help = await File.ReadAllTextAsync(log);
                    Assert.Contains(command == "run" ? "--offline-license" : command, help);
                    if (command == "run") Assert.Contains("GAME_ARGUMENT", help);
                }
            }
            if (definition == RuntimeDefinition.WineGdk && Environment.GetEnvironmentVariable("ORION_TEST_WINEBOOT") == "1")
            {
                var instance = GameInstance.Create("Prepared content fixture", "26.50", "Release");
                await new InstanceRepository(directory.Paths).SaveAsync(instance);
                var content = new Orion.Infrastructure.Content.InstanceContentService(directory.Paths, new());
                await content.ListAsync(instance.Id);
                var packs = Path.Combine(directory.Paths.Prefix(instance.Id), "drive_c/users", Environment.UserName,
                    "AppData/Roaming/Minecraft Bedrock/Users/Shared/games/com.mojang/resource_packs");
                Assert.True(Directory.Exists(packs));
                var sentinel = Path.Combine(packs, "orion-fixture.txt"); await File.WriteAllTextAsync(sentinel, "preserve");
                var environment = new Dictionary<string, string?>
                {
                    ["WINEPREFIX"] = directory.Paths.Prefix(instance.Id), ["WINEDEBUG"] = "-all",
                    ["WINEBOOT_HIDE_DIALOG"] = "1",
                    ["DISPLAY"] = null, ["WAYLAND_DISPLAY"] = null, ["WINEDLLOVERRIDES"] = null,
                    ["WINESERVER"] = RuntimeManager.FindExecutable(output, "wineserver")
                };
                var runner = new ProcessRunner();
                var log = Path.Combine(directory.Root, "wineboot.log");
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try
                {
                    await runner.RunAsync(new(RuntimeManager.FindExecutable(output, "wineboot"), ["-u"], output, environment), log, timeout.Token);
                    // Registry persistence finishes when the private server exits,
                    // not necessarily when wineboot returns (as in GameLauncher).
                    await runner.RunAsync(new(environment["WINESERVER"]!, ["-w"], output, environment), log, timeout.Token);
                    var diagnostics = await File.ReadAllTextAsync(log);
                    Assert.True(File.Exists(Path.Combine(directory.Paths.Prefix(instance.Id), "system.reg")), diagnostics);
                    Assert.True(File.Exists(Path.Combine(directory.Paths.Prefix(instance.Id), "drive_c", "windows", "system32", "d3d12.dll")), diagnostics);
                    Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel));
                }
                finally
                {
                    await WineServer.StopAsync(runner, environment["WINESERVER"]!, output, environment, log);
                }
            }
        }
    }

    private sealed class FixedRelease : IReleaseClient
    {
        public Task<Release> GetLatestAsync(Repository repository, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Release("v2", new("https://example.test/v2"),
                [new(2, "xodus-v2.tar.gz", new("https://example.test/xodus.tar.gz"), 3, "sha256:" + new string('0', 64))]));
    }
}

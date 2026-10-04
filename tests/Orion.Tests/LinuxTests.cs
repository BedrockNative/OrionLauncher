using Orion.Domain;
using Orion.Infrastructure.Linux;
using Orion.Infrastructure.Runtime;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

public sealed class LinuxTests
{
    [Fact]
    public async Task ApplicationEntriesRequirePerInstanceOptInAndCleanUpLegacyEntries()
    {
        using var directory = new TestDirectory();
        var first = GameInstance.Create("Default", "1", "Release");
        var second = GameInstance.Create("Opted in", "1", "Release") with { DesktopShortcut = true };
        Assert.False(first.DesktopShortcut);
        var integration = new DesktopIntegration(directory.Paths, ["orion"], "orion");
        var firstPath = Path.Combine(directory.Paths.Applications, $"io.bedrocknative.orion.{first.Id:N}.desktop");
        var secondPath = Path.Combine(directory.Paths.Applications, $"io.bedrocknative.orion.{second.Id:N}.desktop");
        Directory.CreateDirectory(directory.Paths.Applications);
        await File.WriteAllTextAsync(firstPath, DesktopIntegration.Render(first, ["old-orion"], "orion"));
        var unrelated = Path.Combine(directory.Paths.Applications, "io.bedrocknative.orion.foreign.desktop");
        await File.WriteAllTextAsync(unrelated, "Not managed by Orion");
        var target = Path.Combine(directory.Root, "external.desktop");
        await File.WriteAllTextAsync(target, "X-Orion-Managed=true\n");
        var link = Path.Combine(directory.Paths.Applications, "io.bedrocknative.orion.link.desktop");
        File.CreateSymbolicLink(link, target);
        await integration.SynchronizeAsync([first, second]);
        Assert.False(File.Exists(firstPath));
        Assert.True(File.Exists(secondPath));
        Assert.True(File.Exists(unrelated));
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.True(File.Exists(target));
        await integration.SynchronizeAsync([first, second with { DesktopShortcut = false }]);
        Assert.False(File.Exists(secondPath));
    }

    [Fact]
    public async Task OldInstanceManifestsDefaultToNoShortcutEvenWithLegacyGlobalSetting()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.Paths.Config);
        await File.WriteAllTextAsync(Path.Combine(directory.Paths.Config, "settings.json"),
            "{\"language\":\"en-US\",\"keepInBackground\":true,\"desktopShortcuts\":true}");
        var settings = await new SettingsStore(directory.Paths).LoadAsync();
        Assert.True(settings.KeepInBackground);
        var instance = System.Text.Json.JsonSerializer.Deserialize<GameInstance>(
            "{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Name\":\"Old\",\"Version\":\"1\",\"Channel\":\"Release\",\"CreatedAt\":\"2026-01-01T00:00:00Z\"}")!;
        Assert.False(instance.DesktopShortcut);
        var repository = new InstanceRepository(directory.Paths);
        await repository.SaveAsync(instance with { DesktopShortcut = true });
        Assert.True((await repository.GetAsync(instance.Id)).DesktopShortcut);
    }

    [Fact]
    public void XodusUsesTheSamePrivateProfileAndSocketEverywhere()
    {
        using var directory = new TestDirectory();
        var environment = new XodusEnvironment(directory.Paths).Create(directory.Paths.Prefix(Guid.NewGuid()));
        Assert.Equal(directory.Paths.XodusProfile, environment["XODUS_CONFIG_DIR"]);
        Assert.Equal(Path.Combine(environment["XDG_RUNTIME_DIR"]!, environment["XODUS_SOCK_NAME"]!), environment["XODUS_SOCKET"]);
        Assert.NotEqual("xodus.sock", environment["XODUS_SOCK_NAME"]);
        Assert.Equal($"orion.xodus-{directory.Paths.Identity}.sock", environment["XODUS_SOCK_NAME"]);
        Assert.Null(environment["WINEDLLOVERRIDES"]);
        Assert.StartsWith(directory.Paths.Instances, environment["WINEPREFIX"]);
    }

    [Fact]
    public void DifferentProfilesKeepDistinctIdentifiableSocketsInTheSameRuntimeDirectory()
    {
        using var directory = new TestDirectory();
        var first = directory.Paths;
        var second = new AppPaths(first.Data, Path.Combine(directory.Root, "other-config"),
            first.Cache, first.Runtime, first.Applications);
        Assert.StartsWith("orion.xodus-", first.SocketName);
        Assert.StartsWith("orion.xodus-", second.SocketName);
        Assert.EndsWith(".sock", first.SocketName);
        Assert.EndsWith(".sock", second.SocketName);
        Assert.NotEqual(first.SocketPath, second.SocketPath);
        Assert.Equal(first.Runtime, Path.GetDirectoryName(second.SocketPath));
    }

    [Fact]
    public async Task ShortcutsFollowRenameAndOnlyDeleteOwnedFiles()
    {
        using var directory = new TestDirectory();
        var instance = GameInstance.Create("Test", "1.0", "Release") with { DesktopShortcut = true };
        var integration = new DesktopIntegration(directory.Paths, ["/opt/Orion Launcher/orion"], "/opt/icon.svg");
        await integration.SynchronizeAsync([instance]);
        var path = Directory.GetFiles(directory.Paths.Applications).Single();
        Assert.Contains("--launch", await File.ReadAllTextAsync(path));
        Assert.Contains(instance.Id.ToString(), await File.ReadAllTextAsync(path));
        await integration.SynchronizeAsync([instance with { Name = "New name" }]);
        Assert.Contains("New name", await File.ReadAllTextAsync(path));
        var unrelated = Path.Combine(directory.Paths.Applications, "io.bedrocknative.orion.other.desktop");
        await File.WriteAllTextAsync(unrelated, "[Desktop Entry]\nName=Unrelated\n");
        await integration.SynchronizeAsync([]);
        Assert.False(File.Exists(path)); Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task DoesNotOverwriteForeignOrSymlinkDesktopEntry()
    {
        using var directory = new TestDirectory();
        var instance = GameInstance.Create("Test", "1.0", "Release") with { DesktopShortcut = true };
        Directory.CreateDirectory(directory.Paths.Applications);
        var path = Path.Combine(directory.Paths.Applications, $"io.bedrocknative.orion.{instance.Id:N}.desktop");
        await File.WriteAllTextAsync(path, "foreign");
        var integration = new DesktopIntegration(directory.Paths, ["orion"], "orion");
        await Assert.ThrowsAsync<IOException>(() => integration.SynchronizeAsync([instance]));
        Assert.Equal("foreign", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void DesktopArgumentsEscapePercentAndShellMetacharacters()
    {
        Assert.Equal("\"/opt/Orion Launcher/100%%\"", DesktopIntegration.Argument("/opt/Orion Launcher/100%"));
        Assert.Equal("\"a\\\\$b\"", DesktopIntegration.Argument("a$b"));
        Assert.Throws<ArgumentException>(() => DesktopIntegration.Argument("bad\npath"));
    }

    [Fact]
    public async Task SecondInvocationForwardsInsteadOfAcquiringOwnership()
    {
        using var directory = new TestDirectory();
        await using var primary = await SingleInstance.AcquireAsync(directory.Paths, new());
        Assert.NotNull(primary);
        var received = new TaskCompletionSource<LaunchRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.Listen(request => received.TrySetResult(request));
        var request = new LaunchRequest(Guid.NewGuid(), true);
        await using var secondary = await SingleInstance.AcquireAsync(directory.Paths, request);
        Assert.Null(secondary);
        Assert.Equal(request, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}

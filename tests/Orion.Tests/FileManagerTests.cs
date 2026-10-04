using Orion.Infrastructure.Linux;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

public sealed class FileManagerTests
{
    [Fact]
    public async Task PreferencePersistsAndOldSettingsDefaultToSystem()
    {
        using var dir = new TestDirectory();
        var store = new SettingsStore(dir.Paths);
        Assert.Equal("system", (await store.LoadAsync()).FileManager);
        await store.SaveAsync(new() { FileManager = "dolphin" });
        Assert.Equal("dolphin", (await store.LoadAsync()).FileManager);
    }

    [Fact]
    public void DefaultUsesAssociationAndExplicitChoiceNeverSilentlyFallsBack()
    {
        using var dir = new TestDirectory();
        var launcher = new DesktopFolderLauncher(name => name is "gio" or "dolphin" ? "/usr/bin/" + name : null);
        Assert.Equal(new[] { "open", dir.Root }, launcher.CreateStartInfo(dir.Root, "system").ArgumentList);
        var dolphin = launcher.CreateStartInfo(dir.Root, "dolphin");
        Assert.Equal("/usr/bin/dolphin", dolphin.FileName);
        Assert.Equal(dir.Root, Assert.Single(dolphin.ArgumentList));
        Assert.False(dolphin.UseShellExecute);
        Assert.True(launcher.IsAvailable("system"));
        Assert.False(launcher.IsAvailable("nemo"));
        Assert.Throws<FileNotFoundException>(() => launcher.CreateStartInfo(dir.Root, "nemo"));
        Assert.Throws<ArgumentException>(() => launcher.CreateStartInfo(dir.Root, "sh -c something"));
        Assert.Throws<DirectoryNotFoundException>(() => launcher.CreateStartInfo(Path.Combine(dir.Root, "missing"), "system"));
        var fallback = new DesktopFolderLauncher(name => name == "xdg-open" ? "/usr/bin/xdg-open" : null);
        Assert.Equal(dir.Root, Assert.Single(fallback.CreateStartInfo(dir.Root, "system").ArgumentList));
    }

    [Fact]
    public void BundlePathsAndCredentialsDoNotLeakIntoHostApplications()
    {
        var environment = new Dictionary<string, string?> {
            ["APPDIR"] = "/tmp/bundle", ["APPIMAGE"] = "/apps/Orion.AppImage",
            ["GTK_PATH"] = "/tmp/bundle/usr/lib/gtk", ["FONTCONFIG_FILE"] = "/etc/custom-fonts.conf",
            ["GDK_PIXBUF_MODULE_FILE"] = "/home/user/.cache/orion/pixbuf",
            ["PATH"] = "/usr/bin:/tmp/bundle/usr/bin", ["XDG_DATA_DIRS"] = "/tmp/bundle/usr/share:/usr/share",
            ["LD_LIBRARY_PATH"] = "/custom/lib:/tmp/bundle/usr/lib",
            ["CURSEFORGE_API_KEY"] = "test-not-a-secret", ["ORION_CURSEFORGE_API_KEY_FILE"] = "/private/key",
            ["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/run/user/1000/bus" };
        DesktopFolderLauncher.CleanEnvironment(environment);
        Assert.Equal("/usr/bin", environment["PATH"]);
        Assert.Equal("/usr/share", environment["XDG_DATA_DIRS"]);
        Assert.Equal("/custom/lib", environment["LD_LIBRARY_PATH"]);
        Assert.Equal("/etc/custom-fonts.conf", environment["FONTCONFIG_FILE"]);
        foreach (var key in new[] { "APPDIR", "APPIMAGE", "GTK_PATH", "GDK_PIXBUF_MODULE_FILE", "CURSEFORGE_API_KEY", "ORION_CURSEFORGE_API_KEY_FILE" })
            Assert.False(environment.ContainsKey(key));
        Assert.True(environment.ContainsKey("DBUS_SESSION_BUS_ADDRESS"));
    }

    [Fact]
    public async Task LiteralPathsAndExitErrorsAreHandledWithoutShellInterpolation()
    {
        using var dir = new TestDirectory();
        var folder = Path.Combine(dir.Root, "spaces ' $NOT_EXPANDED ; &"); Directory.CreateDirectory(folder);
        var script = Path.Combine(dir.Root, "file-manager");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nprintf '%s' \"$1\" > received.txt\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var launcher = new DesktopFolderLauncher(_ => script);
        await launcher.OpenAsync(folder, "dolphin");
        Assert.Equal(folder, await File.ReadAllTextAsync(Path.Combine(folder, "received.txt")));
        await File.WriteAllTextAsync(script, "#!/bin/sh\necho 'fixture launch failure' >&2\nexit 4\n");
        var error = await Assert.ThrowsAsync<IOException>(() => launcher.OpenAsync(folder, "dolphin"));
        Assert.Contains("fixture launch failure", error.Message);
        Assert.Contains("4", error.Message);
    }

    [Fact]
    public async Task LongRunningManagerDoesNotBlockOrLoseLateErrors()
    {
        using var dir = new TestDirectory();
        var script = Path.Combine(dir.Root, "manager");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nsleep 2\necho 'late failure' >&2\nexit 3\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await new DesktopFolderLauncher(_ => script).OpenAsync(dir.Root, "dolphin", ex => failure.TrySetResult(ex));
        Assert.False(failure.Task.IsCompleted);
        Assert.Contains("late failure", (await failure.Task.WaitAsync(TimeSpan.FromSeconds(10))).Message);
    }
}

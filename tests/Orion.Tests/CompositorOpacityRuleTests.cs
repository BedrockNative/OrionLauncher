using Orion.Infrastructure.Linux;

namespace Orion.Tests;

public sealed class CompositorOpacityRuleTests
{
    [Fact]
    public async Task OptInRulePreservesConfigAndUndoPreservesLaterUserChanges()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "config.toml");
        const string original = "# personal settings\r\n[appearance]\r\ndrag_opacity = 0.75\r\n";
        await File.WriteAllTextAsync(path, original);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var validated = 0;
        var rules = new CompositorOpacityRule(path, async (candidate, _) =>
        {
            Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(candidate));
            Assert.Contains("# personal settings", await File.ReadAllTextAsync(candidate)); validated++;
        });
        Assert.False(rules.IsInstalled);
        await rules.SetEnabledAsync(true);
        Assert.True(rules.IsInstalled);
        Assert.Equal(original + CompositorOpacityRule.Block, await File.ReadAllTextAsync(path));
        await rules.SetEnabledAsync(true); Assert.Equal(1, validated);
        await File.AppendAllTextAsync(path, "\n# later edit\n");
        await rules.SetEnabledAsync(false);
        Assert.Equal(original + "\n# later edit\n", await File.ReadAllTextAsync(path));
        Assert.False(rules.IsInstalled);
        Assert.Equal(2, Directory.GetFiles(directory.Root, "*.orion-backup-*").Length);
        Assert.Empty(Directory.GetFiles(directory.Root, ".orion-opacity-*.toml"));
    }

    [Fact]
    public async Task RejectsFailedValidationAndConcurrentEditsWithoutOverwriting()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "config.toml");
        await File.WriteAllTextAsync(path, "# original");
        var rules = new CompositorOpacityRule(path, (_, _) => throw new IOException("invalid"));
        await Assert.ThrowsAsync<IOException>(() => rules.SetEnabledAsync(true));
        Assert.Equal("# original", await File.ReadAllTextAsync(path));
        rules = new(path, async (_, _) => await File.WriteAllTextAsync(path, "# changed externally"));
        await Assert.ThrowsAsync<IOException>(() => rules.SetEnabledAsync(true));
        Assert.Equal("# changed externally", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public async Task RefusesEditedManagedBlocksAndSymlinkTargets()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "config.toml");
        await File.WriteAllTextAsync(path, CompositorOpacityRule.Block.Replace("1.0", "0.5"));
        var rules = new CompositorOpacityRule(path, (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => rules.SetEnabledAsync(false));
        var link = Path.Combine(directory.Root, "linked.toml");
        File.CreateSymbolicLink(link, path);
        await Assert.ThrowsAsync<IOException>(() => new CompositorOpacityRule(link).SetEnabledAsync(true));
        Assert.Contains("0.5", await File.ReadAllTextAsync(path));
    }
}

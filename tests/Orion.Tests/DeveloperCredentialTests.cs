using Orion.Infrastructure.CurseForge;
using Orion.Infrastructure.Processes;

namespace Orion.Tests;

public sealed class DeveloperCredentialTests
{
    [Fact]
    public void EnvironmentOverrideWinsWithoutReadingFileOrEmbeddingAKey()
    {
        var result = CurseForgeCredential.Read(name => name == CurseForgeCredential.EnvironmentKey ? "test-developer-key" : "/missing/private-file");
        Assert.Equal("test-developer-key", result);
    }
    [Fact]
    public void PrivateFileSupportsTrailingNewlineAndRejectsPublicPermissions()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Root, "key");
        File.WriteAllText(path, "test-local-key\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        string? Environment(string name) => name == CurseForgeCredential.EnvironmentFile ? path : null;
        Assert.Equal("test-local-key", CurseForgeCredential.Read(Environment));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.OtherRead);
        var exception = Assert.Throws<InvalidOperationException>(() => CurseForgeCredential.Read(Environment));
        Assert.DoesNotContain("test-local-key", exception.ToString());
        Assert.DoesNotContain(path, exception.ToString());
        Assert.Null(exception.InnerException);
    }
    [Theory]
    [InlineData("bad\nkey")]
    [InlineData("bad\0key")]
    [InlineData("   ")]
    public void InvalidKeysHaveSanitizedErrors(string key)
    {
        var error = Assert.Throws<InvalidOperationException>(() => CurseForgeCredential.Read(name => name == CurseForgeCredential.EnvironmentKey ? key : null));
        Assert.DoesNotContain("bad", error.Message);
    }
    [Fact]
    public void RelativeMissingOversizedAndLinkedFilesAreRejected()
    {
        using var directory = new TestDirectory();
        var large = Path.Combine(directory.Root, "large"); File.WriteAllText(large, new string('x', 4097));
        var link = Path.Combine(directory.Root, "link"); File.CreateSymbolicLink(link, large);
        foreach (var path in new[] { "relative-file", Path.Combine(directory.Root, "missing"), large, link })
            Assert.Throws<InvalidOperationException>(() => CurseForgeCredential.Read(name => name == CurseForgeCredential.EnvironmentFile ? path : null));
    }
    [Fact]
    public void OverridesAreNeverForwardedToGameOrSupervisorEvenIfRequestedAsGameEnvironment()
    {
        var command = new ProcessCommand("unused", [], "/tmp", new Dictionary<string, string?>
        {
            [CurseForgeCredential.EnvironmentKey] = "test-secret", [CurseForgeCredential.EnvironmentFile] = "/private/key", ["ORION_TEST_NORMAL"] = "keep"
        });
        foreach (var info in new[] { ProcessRunner.StartInfo(command), new ProcessRunner(["orion"]).OwnedStartInfo(command) })
        {
            Assert.False(info.Environment.ContainsKey(CurseForgeCredential.EnvironmentKey));
            Assert.False(info.Environment.ContainsKey(CurseForgeCredential.EnvironmentFile));
            Assert.Equal("keep", info.Environment["ORION_TEST_NORMAL"]);
        }
    }
}

using System.Formats.Tar;
using System.IO.Compression;
using Orion.Infrastructure.Releases;

namespace Orion.Tests;

public sealed class ArchiveTests
{
    [Theory]
    [InlineData("../escaped")]
    [InlineData("bin/../../escaped")]
    [InlineData("/tmp/escaped")]
    public async Task RejectsPathsOutsideDestination(string name)
    {
        using var directory = new TestDirectory();
        var archive = await CreateAsync(directory.Root, [(name, TarEntryType.RegularFile, "")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => TarArchive.ExtractAsync(archive, Path.Combine(directory.Root, "out"), CancellationToken.None));
    }

    [Fact]
    public async Task PreservesExecutablePermissionsAndInternalLinks()
    {
        using var directory = new TestDirectory();
        var archive = await CreateAsync(directory.Root, [("bin/wine", TarEntryType.RegularFile, ""), ("bin/wineboot", TarEntryType.SymbolicLink, "wine")]);
        var output = Path.Combine(directory.Root, "out");
        await TarArchive.ExtractAsync(archive, output, CancellationToken.None);
        Assert.True((File.GetUnixFileMode(Path.Combine(output, "bin/wine")) & UnixFileMode.UserExecute) != 0);
        Assert.Equal("wine", new FileInfo(Path.Combine(output, "bin/wineboot")).LinkTarget);
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(output, "bin/wineboot")));
    }

    [Theory]
    [InlineData("../../outside")]
    [InlineData("/tmp/outside")]
    public async Task RejectsEscapingLinks(string link)
    {
        using var directory = new TestDirectory();
        var archive = await CreateAsync(directory.Root, [("bin/link", TarEntryType.SymbolicLink, link)]);
        await Assert.ThrowsAsync<InvalidDataException>(() => TarArchive.ExtractAsync(archive, Path.Combine(directory.Root, "out"), CancellationToken.None));
    }

    [Fact]
    public async Task RefusesLinkParentsEvenWhenDeclaredAfterChildren()
    {
        using var directory = new TestDirectory();
        var archive = await CreateAsync(directory.Root, [("parent/child", TarEntryType.SymbolicLink, "../file"), ("parent", TarEntryType.SymbolicLink, "folder")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => TarArchive.ExtractAsync(archive, Path.Combine(directory.Root, "out"), CancellationToken.None));
    }

    internal static async Task<string> CreateAsync(string root, (string Name, TarEntryType Type, string Link)[] entries)
    {
        var path = Path.Combine(root, $"{Guid.NewGuid():N}.tar.gz");
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        await using var writer = new TarWriter(gzip);
        foreach (var (name, type, link) in entries)
        {
            var entry = new PaxTarEntry(type, name) { Mode = (UnixFileMode)0x1ED };
            if (type == TarEntryType.RegularFile) entry.DataStream = new MemoryStream("payload"u8.ToArray());
            else entry.LinkName = link;
            await writer.WriteEntryAsync(entry);
            entry.DataStream?.Dispose();
        }
        return path;
    }

    [Fact]
    public async Task GnuSparseBinaryIsReconstructedWithItsRealName()
    {
        using var directory = new TestDirectory();
        var archive = Path.Combine(directory.Root, "sparse.tar.gz");
        await using (var file = File.Create(archive))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var writer = new TarWriter(gzip))
        {
            var payload = new byte[515];
            "2\n0\n2\n9\n1\n"u8.CopyTo(payload);
            payload[512] = (byte)'a'; payload[513] = (byte)'b'; payload[514] = (byte)'c';
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "GNUSparseFile.0/tool", new Dictionary<string, string>
            {
                ["GNU.sparse.major"] = "1", ["GNU.sparse.minor"] = "0", ["GNU.sparse.name"] = "tool",
                ["GNU.sparse.realsize"] = "10"
            }) { Mode = (UnixFileMode)0x1ED, DataStream = new MemoryStream(payload) };
            await writer.WriteEntryAsync(entry);
        }
        var output = Path.Combine(directory.Root, "out");
        await TarArchive.ExtractAsync(archive, output, CancellationToken.None);
        Assert.Equal(new byte[] { 97, 98, 0, 0, 0, 0, 0, 0, 0, 99 }, await File.ReadAllBytesAsync(Path.Combine(output, "tool")));
        Assert.False(Directory.Exists(Path.Combine(output, "GNUSparseFile.0")));
    }
}

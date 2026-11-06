using DiscUtils;
using Orion.Domain;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Linux;

namespace Orion.Tests;

public sealed class PackageExtractionTests
{
    private sealed class FixtureFileSystem : ReadOnlyDiscFileSystem
    {
        private readonly Dictionary<string, byte[]> files = new()
        {
            ["Minecraft.Windows.exe"] = OrionOptimizationTests.Image(),
            ["nested\\asset.txt"] = System.Text.Encoding.UTF8.GetBytes("content"),
            ["zero.dat"] = []
        };
        private static string Normalize(string path) => path.Trim('\\', '/');
        public override string FriendlyName => "Extraction fixture";
        public override long Size => 4096;
        public override long UsedSpace => 4096;
        public override long AvailableSpace => 0;
        public override bool DirectoryExists(string path) => Normalize(path) is "" or "nested" or "empty";
        public override bool FileExists(string path) => files.ContainsKey(Normalize(path));
        public override string[] GetDirectories(string path, string pattern, SearchOption option) => Normalize(path) == "" ? ["nested", "empty"] : [];
        public override string[] GetFiles(string path, string pattern, SearchOption option) =>
            files.Keys.Where(k => (k.Contains('\\') ? k[..k.LastIndexOf('\\')] : "") == Normalize(path)).ToArray();
        public override string[] GetFileSystemEntries(string path) => [.. GetDirectories(path, "*", SearchOption.TopDirectoryOnly), .. GetFiles(path, "*", SearchOption.TopDirectoryOnly)];
        public override string[] GetFileSystemEntries(string path, string pattern) => GetFileSystemEntries(path);
        public override FileAttributes GetAttributes(string path) => DirectoryExists(path) ? FileAttributes.Directory : FileAttributes.Normal;
        public override DateTime GetCreationTimeUtc(string path) => DateTime.UnixEpoch;
        public override DateTime GetLastAccessTimeUtc(string path) => DateTime.UnixEpoch;
        public override DateTime GetLastWriteTimeUtc(string path) => DateTime.UnixEpoch;
        public override long GetFileLength(string path) => files[Normalize(path)].Length;
        public override DiscUtils.Streams.SparseStream OpenFile(string path, FileMode mode, FileAccess access) =>
            DiscUtils.Streams.SparseStream.FromStream(new MemoryStream(files[Normalize(path)], false), DiscUtils.Streams.Ownership.Dispose);
    }

    private sealed class Reporter : IProgress<OperationProgress>
    {
        public List<OperationProgress> Values { get; } = [];
        public void Report(OperationProgress value) => Values.Add(value);
    }

    [Fact]
    public void ExtractsNestedFilesEvenWhenGenericEnumerationReturnsBaseObjects()
    {
        using var d = new TestDirectory();
        var source = Path.Combine(d.Root, "source"); var output = Path.Combine(d.Root, "output");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllBytes(Path.Combine(source, "Minecraft.Windows.exe"), OrionOptimizationTests.Image());
        File.WriteAllText(Path.Combine(source, "nested", "asset.txt"), "content");
        File.WriteAllBytes(Path.Combine(source, "zero.dat"), []);
        using var fs = new FixtureFileSystem();
        Assert.All(fs.Root.GetFileSystemInfos(), entry => Assert.IsType<DiscFileSystemInfo>(entry));
        var progress = new Reporter();
        XvdPackageExtractor.ExtractFilesystem(fs, output, progress, default);
        Assert.Equal(OrionOptimizationTests.Image(), File.ReadAllBytes(Path.Combine(output, "Minecraft.Windows.exe")));
        Assert.Equal("content", File.ReadAllText(Path.Combine(output, "nested", "asset.txt")));
        Assert.True(Directory.Exists(Path.Combine(output, "empty")));
        Assert.Equal(3, Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length);
        var reports = progress.Values.Where(v => v.Fraction is not null).ToArray();
        Assert.Equal(0d, reports[0].Fraction); Assert.Equal(1d, reports[^1].Fraction);
        Assert.Contains("3 / 3", reports[^1].Message);
        Assert.All(reports, p => Assert.InRange(p.Fraction!.Value, 0, 1));
    }

    [Fact]
    public void ExtractsHiddenAndSystemFilesFromNtfsPackages()
    {
        // DiscUtils cannot format NTFS on Linux; ntfs-3g's offline tools build the volume.
        if (DesktopFolderLauncher.FindExecutable("mkntfs") is not { } mkntfs || DesktopFolderLauncher.FindExecutable("ntfscp") is not { } ntfscp) return;
        using var d = new TestDirectory();
        var image = Path.Combine(d.Root, "volume.img"); var exe = Path.Combine(d.Root, "game.bin"); var config = Path.Combine(d.Root, "game.xml");
        using (var create = File.Create(image)) create.SetLength(2 * 1024 * 1024);
        File.WriteAllBytes(exe, OrionOptimizationTests.Image()); File.WriteAllText(config, "<Game />");
        void Run(string file, params string[] args)
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        Run(mkntfs, "-F", "-Q", "-q", "-s", "512", "-c", "4096", image);
        Run(ntfscp, "-q", image, exe, "Minecraft.Windows.exe");
        Run(ntfscp, "-q", image, config, "MicrosoftGame.config");
#pragma warning disable CA1416
        using (var stream = File.Open(image, FileMode.Open, FileAccess.ReadWrite))
        using (var ntfs = new DiscUtils.Ntfs.NtfsFileSystem(stream))
        {
            ntfs.SetAttributes("Minecraft.Windows.exe", FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
            ntfs.SetAttributes("MicrosoftGame.config", FileAttributes.Hidden | FileAttributes.Archive);
        }
#pragma warning restore CA1416
        using var volume = File.OpenRead(image);
        using var fs = XvdPackageExtractor.OpenFilesystem(volume);
        var output = Path.Combine(d.Root, "output");
        XvdPackageExtractor.ExtractFilesystem(fs, output, null, default);
        Assert.Equal(OrionOptimizationTests.Image(), File.ReadAllBytes(Path.Combine(output, "Minecraft.Windows.exe")));
        Assert.Equal("<Game />", File.ReadAllText(Path.Combine(output, "MicrosoftGame.config")));
        Assert.Equal(new[] { "MicrosoftGame.config", "Minecraft.Windows.exe" }, Directory.EnumerateFileSystemEntries(output).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(Path.Combine(output, "Minecraft.Windows.exe"), GameLayout.Executable(output));
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(5L, true)]
    [InlineData(11L, true)]
    [InlineData(15L, true)]
    [InlineData(16L, false)]
    [InlineData(19L, false)] // Minecraft 1.26.62 stores Minecraft.Windows.exe here.
    [InlineData(23L, false)]
    [InlineData((3L << 48) | 5, true)]
    [InlineData((7L << 48) | 19, false)]
    public void TreatsOnlyReservedNtfsRecordsAsMetafiles(long fileId, bool metafile) =>
        Assert.Equal(metafile, XvdPackageExtractor.IsNtfsMetafile(fileId));

    [Fact]
    public void CancellationStopsBeforeCreatingFiles()
    {
        using var d = new TestDirectory(); var source = Path.Combine(d.Root, "source");
        Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "a"), "content");
        using var fs = new FixtureFileSystem();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var output = Path.Combine(d.Root, "output");
        Assert.Throws<OperationCanceledException>(() => XvdPackageExtractor.ExtractFilesystem(fs, output, null, cts.Token));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void BlockProgressIsBoundedThrottledAndFinishesAtOneHundredPercent()
    {
        var progress = new Reporter(); var phase = new PackagePhaseProgress("Decrypting blocks", 1000, progress);
        phase.Report(0);
        for (var i = 1; i <= 1000; i++) phase.Report(i);
        phase.Complete();
        Assert.Equal(0d, progress.Values[0].Fraction);
        Assert.Equal(1d, progress.Values[^1].Fraction);
        Assert.True(progress.Values.Count < 20);
        Assert.All(progress.Values, p => Assert.InRange(p.Fraction!.Value, 0, 1));
    }
}

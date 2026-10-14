using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using Orion.Domain;
using Orion.Application;
using Orion.Infrastructure.Releases;
using System.Security.Cryptography;
using Orion.Desktop.ViewModels;
using Orion.Infrastructure.Games;

namespace Orion.Tests;

public sealed class OrionOptimizationTests
{
    public static byte[] Image()
    {
        var bytes = new byte[0x600];
        void U16(int p, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(p), v);
        void U32(int p, int v) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(p), v);
        U16(0, 0x5a4d); U32(0x3c, 0x80); U32(0x80, 0x4550);
        U16(0x84, 0x8664); U16(0x86, 1); U16(0x94, 240); U16(0x96, 0x22);
        const int o = 0x98;
        U16(o, 0x20b); U32(o + 32, 0x1000); U32(o + 36, 0x200);
        U32(o + 56, 0x2000); U32(o + 60, 0x400); U32(o + 108, 16);
        U32(o + 120, 0x1000); U32(o + 124, 40);
        const int s = o + 240;
        Encoding.ASCII.GetBytes(".text").CopyTo(bytes, s);
        U32(s + 8, 0x200); U32(s + 12, 0x1000); U32(s + 16, 0x200); U32(s + 20, 0x400);
        U32(s + 36, 0x60000020);
        U32(0x400, 0x1060); U32(0x40c, 0x1040); U32(0x410, 0x1080);
        Encoding.ASCII.GetBytes("kernel32.dll\0").CopyTo(bytes, 0x440);
        return bytes;
    }

    [Fact]
    public void OffByDefaultAndPersistsInInstanceEditor()
    {
        Assert.False(JsonSerializer.Deserialize<InstanceLaunchOptions>("{}")!.OrionOptimizations);
        var instance = GameInstance.Create("Optimization test", "26.52", "Release");
        var editor = new LaunchOptionsViewModel(instance);
        Assert.False(editor.OrionOptimizations);
        editor.OrionOptimizations = true;
        var saved = JsonSerializer.Deserialize<InstanceLaunchOptions>(JsonSerializer.Serialize(editor.Build()))!;
        Assert.True(saved.OrionOptimizations);
        editor.ResetCommand.Execute(null);
        Assert.False(editor.Build().OrionOptimizations);
    }

    [Fact]
    public void AddsImportWithoutMovingCodeOrExistingIat()
    {
        var original = Image(); var saved = original.ToArray();
        var patched = OptimizationImport.Add(original, "Z:\\cache\\Orion.Optimization.dll");
        Assert.Equal(saved, original);
        using var pe = new PEReader(new MemoryStream(patched));
        Assert.Equal(2, pe.PEHeaders.SectionHeaders.Length);
        Assert.Equal(0x1000, pe.PEHeaders.SectionHeaders[0].VirtualAddress);
        Assert.Equal(original.AsSpan(0x400, 0x200).ToArray(),
            pe.GetSectionData(0x1000).GetContent(0, 0x200).ToArray());
        var section = pe.PEHeaders.SectionHeaders[1];
        Assert.Equal(section.VirtualAddress, pe.PEHeaders.PEHeader!.ImportTableDirectory.RelativeVirtualAddress);
        Assert.Equal(original.AsSpan(0x400, 20).ToArray(), patched.AsSpan(section.PointerToRawData, 20).ToArray());
        Assert.Contains("OrionOptimizationInitialize\0", Encoding.ASCII.GetString(patched));
        Assert.Contains("Z:\\cache\\Orion.Optimization.dll\0", Encoding.ASCII.GetString(patched));
    }

    [Fact]
    public void RejectsOtherArchitectures()
    {
        var source = Image(); BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(0x84), 0x14c);
        Assert.Throws<InvalidDataException>(() => OptimizationImport.Add(source, "mod.dll"));
    }

    private sealed class Releases(Release release) : IReleaseClient
    {
        public bool Offline;
        public Task<Release> GetLatestAsync(Repository repository, CancellationToken ct = default) =>
            Offline ? Task.FromException<Release>(new HttpRequestException("offline")) : Task.FromResult(release);
    }
    private sealed class Download(byte[] bytes) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    [Fact]
    public async Task DownloadsOnceReusesExecutableAndWorksOffline()
    {
        using var directory = new TestDirectory();
        var dll = Image();
        BinaryPrimitives.WriteUInt16LittleEndian(dll.AsSpan(0x96), 0x2022);
        BinaryPrimitives.WriteInt32LittleEndian(dll.AsSpan(0x98 + 112), 0x1000);
        BinaryPrimitives.WriteInt32LittleEndian(dll.AsSpan(0x98 + 116), 40);
        var asset = new ReleaseAsset(123, "Orion.Optimization.dll", new("https://example.org/mod.dll"), dll.Length,
            "sha256:" + Convert.ToHexString(SHA256.HashData(dll)));
        var releases = new Releases(new("v1", new("https://example.org/release"), [asset]));
        var download = new Download(dll); using var http = new HttpClient(download);
        var manager = new OrionOptimization(directory.Paths, releases, new(http));
        var id = Guid.NewGuid();
        var game = directory.Paths.Game(id); Directory.CreateDirectory(game);
        var exe = Path.Combine(game, "Minecraft.Windows.exe"); await File.WriteAllBytesAsync(exe, Image());
        var first = await manager.PrepareAsync(id, exe, null, default);
        var written = File.GetLastWriteTimeUtc(first);
        Assert.Contains("OrionOptimizationInitialize", Encoding.ASCII.GetString(await File.ReadAllBytesAsync(first)));
        releases.Offline = true;
        var second = await manager.PrepareAsync(id, exe, null, default);
        Assert.Equal(first, second); Assert.Equal(1, download.Calls);
        Assert.Equal(written, File.GetLastWriteTimeUtc(second));
        Assert.False(File.Exists(Path.Combine(directory.Paths.Instance(id), "launch-optimized-wine")));
        File.Delete(second);
        Assert.True(File.Exists(await manager.PrepareAsync(id, exe, null, default)));
        File.SetLastWriteTimeUtc(first, DateTime.UnixEpoch);
        File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddMinutes(1));
        await manager.PrepareAsync(id, exe, null, default);
        Assert.NotEqual(DateTime.UnixEpoch, File.GetLastWriteTimeUtc(first));
        Assert.Equal(Image(), await File.ReadAllBytesAsync(exe));

    }

    [Fact]
    public void ReleaseSelectionRequiresExactDllAsset()
    {
        var asset = new ReleaseAsset(1, "Orion.Optimization.dll", new("https://example.org/mod.dll"), 1024, null);
        var release = new Release("v1", new("https://example.org/release"), [asset]);
        Assert.Equal(asset, OrionOptimization.SelectAsset(release));
        Assert.Throws<InvalidDataException>(() => OrionOptimization.SelectAsset(release with { Assets = [] }));
    }
}

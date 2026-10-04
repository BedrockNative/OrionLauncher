using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Orion.Domain;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Rtx;

namespace Orion.Tests;

public sealed class RtxParityTests
{
    internal const string FormJson = """
        {"categories":[{"id":"light","label":"Light","fields":[
          {"name":"enabled","label":"Enabled","type":"toggle","macro":"ENABLED","default":true},
          {"name":"power","label":"Power","type":"slider","macro":"POWER","min":0,"max":10,"step":0.1,"default":2,"macroScale":2,"showWhen":{"field":"enabled","value":true}},
          {"name":"mode","label":"Mode","type":"select","macro":"MODE","options":[{"label":"Soft"},{"label":"Sharp"}],"macroMap":{"Soft":0,"Sharp":1},"default":"Soft"},
          {"name":"color","label":"Color","type":"color","macro":"COLOR","default":[1,0.5,0],"min":0,"max":1,"step":0.01,"intensityField":"intensity"},
          {"name":"intensity","label":"Intensity","type":"slider","macro":"INTENSITY","min":0,"max":4,"step":0.1,"default":2,"consumedBy":"color"}
        ]}]}
        """;
    private static RtxService Service(TestDirectory dir) { var activity = new InstanceActivity(); return new(dir.Paths, activity, new RtxTests.Catalog(), new(dir.Paths, activity)); }
    private static string Materials(TestDirectory dir, GameInstance instance) => Path.Combine(dir.Paths.Game(instance.Id), "data/renderer/materials");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptReceiptIsNeverReportedAsVanillaAndCanRecover(bool corrupt)
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir); var service = Service(dir);
        await service.InstallAsync(instance, RtxTests.Preset(), false, null, default);
        var first = (await service.InspectAsync(instance.Id)).Installation!;
        var receipt = Path.Combine(dir.Paths.Instance(instance.Id), "rtx/current.json");
        if (corrupt) await File.WriteAllTextAsync(receipt, "{"); else File.Delete(receipt);
        var state = await service.InspectAsync(instance.Id);
        Assert.NotNull(state.VerificationError); Assert.True(state.CanRestore); Assert.True(state.Installation!.Recovered);
        Assert.Throws<IOException>(() => service.ValidateForLaunchUnderLease(instance.Id));
        await service.RestoreAsync(instance.Id); Assert.Null((await service.InspectAsync(instance.Id)).VerificationError);
        Assert.True(File.Exists(Path.Combine(dir.Paths.Instance(instance.Id), "rtx/quarantine", first.Folder, "RTXStub.material.bin")));
        service.ValidateForLaunchUnderLease(instance.Id);
    }
    [Fact]
    public async Task OrphanCleanupIsIdempotentAndNeverTouchesActiveShaders()
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir); var service = Service(dir);
        await service.InstallAsync(instance, RtxTests.Preset(), false, null, default);
        var active = (await service.InspectAsync(instance.Id)).Installation!;
        var name = "orion-rtx-" + Guid.NewGuid().ToString("N"); var orphan = Path.Combine(Materials(dir, instance), name);
        Directory.CreateDirectory(orphan); await File.WriteAllTextAsync(Path.Combine(orphan, "unknown.txt"), "keep me");
        await service.InspectAsync(instance.Id); await service.InspectAsync(instance.Id);
        Assert.True(Directory.Exists(Path.Combine(Materials(dir, instance), active.Folder)));
        Assert.False(Directory.Exists(orphan)); Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(dir.Paths.Instance(instance.Id), "rtx/quarantine", name, "unknown.txt")));
    }
    [Fact]
    public async Task CommittedCleanupRecordSurvivesRestartAndRemovesOnlyVerifiedPreviousShader()
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir); var service = Service(dir);
        await service.InstallAsync(instance, RtxTests.Preset(), false, null, default);
        var old = (await service.InspectAsync(instance.Id)).Installation!;
        await service.InstallAsync(instance, RtxTests.Preset("new"), false, null, default);
        var oldPath = Path.Combine(Materials(dir, instance), old.Folder); Directory.CreateDirectory(oldPath);
        await File.WriteAllBytesAsync(Path.Combine(oldPath, "RTXStub.material.bin"), RtxTests.Shader(9));
        var cleanup = Path.Combine(dir.Paths.Instance(instance.Id), "rtx/cleanup.json");
        await File.WriteAllTextAsync(cleanup, JsonSerializer.Serialize(new { Materials = "game/data/renderer/materials", Installation = old }));
        var after = await Service(dir).InspectAsync(instance.Id);
        Assert.Equal("new", after.Installation!.Name); Assert.Null(after.VerificationError); Assert.False(Directory.Exists(oldPath)); Assert.False(File.Exists(cleanup));
    }
    [Fact]
    public async Task RtpackBackupsRoundTripWithoutOverwritingOriginalsOrExistingExport()
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir); var service = Service(dir);
        await service.InstallAsync(instance, RtxTests.Preset(), false, null, default);
        var file = Path.Combine(dir.Root, "preset.rtpack"); await service.ExportAsync(instance.Id, file, false);
        using (var zip = ZipFile.OpenRead(file)) Assert.NotNull(zip.GetEntry("RTXStub.material.bin"));
        var bytes = await File.ReadAllBytesAsync(file);
        await Assert.ThrowsAsync<IOException>(() => service.ExportAsync(instance.Id, file, true)); Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        await service.RestoreAsync(instance.Id); await service.ImportAsync(instance, file, true, default);
        Assert.Null((await service.InspectAsync(instance.Id)).VerificationError);
        Assert.Equal(RtxTests.Shader(1), await File.ReadAllBytesAsync(Path.Combine(Materials(dir, instance), "RTXStub.material.bin")));
        Assert.Empty(Directory.GetFiles(dir.Root, "*.tmp"));
    }
    [Fact]
    public async Task BatchReportsFailureAndCancellationWithoutUndoingCompletedInstances()
    {
        var a = GameInstance.Create("a", "26.40.1", "Release"); var b = a with { Id = Guid.NewGuid(), Name = "b" }; var c = b with { Id = Guid.NewGuid(), Name = "c" };
        using var ct = new CancellationTokenSource();
        var results = await RtxBatch.RunAsync([a, a, b, c], (i, _) => { if (i.Id == b.Id) { ct.Cancel(); throw new IOException("fixture failure"); } return Task.CompletedTask; }, ct.Token);
        Assert.Equal(3, results.Count); Assert.True(results[0].Success); Assert.Contains("fixture failure", results[1].Error); Assert.Contains("not started", results[2].Error);
    }
    internal static byte[] Dll(byte marker)
    {
        var bytes = new byte[256]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 64);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(64)); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(86), 0x2000); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(88), 0x20b); bytes[255] = marker; return bytes;
    }
    [Fact]
    public async Task DlssPreservesFirstBackupAndQuarantinesExternallyChangedDll()
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir); var service = Service(dir);
        var target = Path.Combine(dir.Paths.Game(instance.Id), "nvngx_dlss.dll"); var file = Path.Combine(dir.Root, "local.dll");
        await File.WriteAllBytesAsync(target, Dll(1)); await File.WriteAllBytesAsync(file, Dll(2));
        await service.InstallDlssAsync(instance.Id, file, "local"); await File.WriteAllBytesAsync(file, Dll(3));
        await service.InstallDlssAsync(instance.Id, file, "local again");
        Assert.Equal(Dll(1), await File.ReadAllBytesAsync(Path.Combine(dir.Paths.Instance(instance.Id), "rtx/dlss/original.dll")));
        await File.WriteAllBytesAsync(target, Dll(4)); Assert.NotNull((await service.InspectDlssAsync(instance.Id)).Error);
        await service.RestoreDlssAsync(instance.Id); Assert.Equal(Dll(1), await File.ReadAllBytesAsync(target));
        Assert.Equal(Dll(4), await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(dir.Paths.Instance(instance.Id), "rtx/dlss"), "quarantine-*.dll"))));
        Assert.False((await service.InspectDlssAsync(instance.Id)).CanRestore);
    }
    [Fact]
    public async Task DlssRejectsInvalidInputsAndCorruptBackupWithoutChangingGame()
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir); var service = Service(dir);
        var target = Path.Combine(dir.Paths.Game(instance.Id), "nvngx_dlss.dll"); var file = Path.Combine(dir.Root, "local.dll");
        await File.WriteAllBytesAsync(target, Dll(1)); await File.WriteAllTextAsync(file, "invalid");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallDlssAsync(instance.Id, file, "local"));
        Assert.Equal(Dll(1), await File.ReadAllBytesAsync(target)); await File.WriteAllBytesAsync(file, Dll(2)); await service.InstallDlssAsync(instance.Id, file, "local");
        await File.WriteAllBytesAsync(Path.Combine(dir.Paths.Instance(instance.Id), "rtx/dlss/original.dll"), Dll(8));
        await Assert.ThrowsAsync<IOException>(() => service.RestoreDlssAsync(instance.Id)); Assert.Equal(Dll(2), await File.ReadAllBytesAsync(target));
    }
    [Theory]
    [InlineData("https://evil.test/a")]
    [InlineData("brtx://preset/x?cmd=bad")]
    [InlineData("brtx://user@preset/x")]
    [InlineData("brtx://creator/a%2Fb")]
    [InlineData("brtx://preset/a/b")]
    public void LinksRejectUntrustedInput(string link) => Assert.Throws<ArgumentException>(() => RtxLink.Parse(link));

    [Fact]
    public void CreatorSerializesAllFieldKindsWithDependenciesAndColorIntensity()
    {
        var form = RtxCreatorForm.Parse(FormJson); var values = form.Defaults();
        using var json = JsonDocument.Parse(form.SerializeSettings(values));
        Assert.Equal(4, json.RootElement.GetProperty("POWER").GetDouble()); Assert.Equal("float4(1, 0.5, 0, 2)", json.RootElement.GetProperty("COLOR").GetString());
        Assert.False(json.RootElement.TryGetProperty("INTENSITY", out _));
        values["enabled"] = JsonSerializer.SerializeToElement(false); Assert.DoesNotContain("POWER", form.SerializeSettings(values));
        values["power"] = JsonSerializer.SerializeToElement(11); Assert.Throws<InvalidDataException>(() => form.SerializeSettings(values));
        Assert.Throws<InvalidDataException>(() => RtxCreatorForm.Parse(FormJson.Replace("\"field\":\"enabled\"", "\"field\":\"power\"")));
        var beta = RtxCreatorForm.Parse(FormJson.Replace("\"field\":\"enabled\"", "\"field\":\"removedParent\""));
        Assert.DoesNotContain("POWER", beta.SerializeSettings(beta.Defaults()));
    }
    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { var response = respond(request); response.RequestMessage = request; return Task.FromResult(response); }
    }
    [Fact]
    public async Task CreatorBuildUsesCurrentApiAndNeverInstallsOrAcceptsUntrustedJobIds()
    {
        using var dir = new TestDirectory(); var requests = new List<string>();
        using var http = new HttpClient(new Handler(request => { requests.Add(request.RequestUri!.AbsoluteUri); return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"job-123\",\"status\":\"completed\"}") }; }));
        var catalog = new RtxCatalog(http, new GitHubReleaseClient(http, dir.Root), dir.Root);
        var form = RtxCreatorForm.Parse(FormJson); var result = await catalog.BuildCreatorAsync("1.4.4", form, form.Defaults(), null, default);
        Assert.Single(requests); Assert.Equal("https://bedrock.graphics/api/build?version=1.4.4", requests[0]);
        Assert.Equal(3, result.Materials.Count); Assert.All(result.Materials.Values, url => Assert.StartsWith("https://bedrock.graphics/api/build/job-123/file/", url.AbsoluteUri));
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.BuildCreatorAsync("../escape", form, form.Defaults(), null, default));
    }
    [Fact]
    public async Task CreatorRespectsAntiBotInsteadOfRetryingOrBypassingIt()
    {
        using var dir = new TestDirectory(); var count = 0;
        using var http = new HttpClient(new Handler(_ => { count++; return new(HttpStatusCode.Forbidden); }));
        var catalog = new RtxCatalog(http, new GitHubReleaseClient(http, dir.Root), dir.Root); var form = RtxCreatorForm.Parse(FormJson);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.BuildCreatorAsync("1.4.4", form, form.Defaults(), null, default));
        Assert.Contains("anti-bot", error.Message); Assert.Equal(1, count);
    }
}

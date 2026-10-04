using Orion.Domain;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Rtx;

namespace Orion.Tests;

public sealed class RtxRuntimeEnvironmentTests
{
    [Theory]
    [InlineData("11.18-7-winrt", false)]
    [InlineData("11.18-8-winrt", true)]
    [InlineData("11.18-10-winrt", true)]
    [InlineData("11.19-1-winrt", true)]
    [InlineData("11.17-99-winrt", false)]
    [InlineData("unknown", false)]
    public void OlderBundledRuntimeDoesNotSilentlyMissNgxSetup(string tag, bool expected)
        => Assert.Equal(expected, RtxRuntimeEnvironment.SupportsNgxDiscovery(tag));

    [Theory]
    [InlineData(RtxFamily.BetterRtx)]
    [InlineData(RtxFamily.VanillaRtx)]
    public async Task BothFamiliesEnableRuntimeDefaultsWithoutSharingConfiguration(RtxFamily family)
    {
        using var dir = new TestDirectory(); var instance = await RtxTests.Setup(dir);
        var activity = new InstanceActivity(); var content = new InstanceContentService(dir.Paths, activity);
        var service = new RtxService(dir.Paths, activity, new RtxTests.Catalog(), content);
        Assert.False(service.UsesRtxUnderLease(instance.Id));
        await service.ConfigureAsync(instance.Id, new(true), family: family);
        Assert.True(service.UsesRtxUnderLease(instance.Id));
        var other = family == RtxFamily.BetterRtx ? RtxFamily.VanillaRtx : RtxFamily.BetterRtx;
        Assert.False((await service.InspectAsync(instance.Id, family: other)).Configuration.EnableOnLaunch);
        var env = RtxRuntimeEnvironment.Apply(service.UsesRtxUnderLease(instance.Id), "/driver", true, new Dictionary<string, string?>());
        Assert.Equal("1", env["DXVK_ENABLE_NVAPI"]);
        Assert.Equal("/driver", env["NVIDIA_WINE_DLL_DIR"]);
        Assert.Equal("NVIDIA_only", env["__VK_LAYER_NV_optimus"]);
        Assert.Equal("DLSSIndicator=0", env["DXVK_NVAPI_SET_NGX_DEBUG_OPTIONS"]);
        Assert.Equal("0", env["__NGX_SHOW_INDICATOR"]);
        await service.ConfigureAsync(instance.Id, new(), family: family);
        Assert.False(service.UsesRtxUnderLease(instance.Id));
    }

    [Fact]
    public void InactiveAndMissingNvidiaDoNotForceGpuOrNvapi()
    {
        var source = new Dictionary<string, string?> { ["WINEPREFIX"] = "/private" };
        Assert.Equal(source, RtxRuntimeEnvironment.Apply(false, "/driver", true, source));
        var absent = RtxRuntimeEnvironment.Apply(true, null, false, source);
        Assert.False(absent.ContainsKey("DXVK_ENABLE_NVAPI"));
        Assert.False(absent.ContainsKey("__NV_PRIME_RENDER_OFFLOAD"));
        Assert.False(RtxRuntimeEnvironment.Apply(true, "/driver", false, source).ContainsKey("__NV_PRIME_RENDER_OFFLOAD"));
        Assert.Single(source);
    }

    [Fact]
    public void OffloadDetectionRequiresNvidiaPciVendorAndBoundProprietaryDriver()
    {
        using var dir = new TestDirectory(); var devices = Path.Combine(dir.Paths.Data, "pci");
        var device = Path.Combine(devices, "0000:01:00.0"); Directory.CreateDirectory(device);
        File.WriteAllText(Path.Combine(device, "vendor"), "0x10de\n");
        Assert.False(RtxRuntimeEnvironment.HasNvidiaDriver(devices));
        Directory.CreateSymbolicLink(Path.Combine(device, "driver"), "/sys/bus/pci/drivers/nvidia");
        Assert.True(RtxRuntimeEnvironment.HasNvidiaDriver(devices));
        File.WriteAllText(Path.Combine(device, "vendor"), "0x8086\n");
        Assert.False(RtxRuntimeEnvironment.HasNvidiaDriver(devices));
    }

    [Fact]
    public void ExplicitInstanceOverridesRemainAuthoritative()
    {
        var defaults = RtxRuntimeEnvironment.Apply(true, "/driver", true, new Dictionary<string, string?>());
        var options = new InstanceLaunchOptions { Environment = new()
        {
            ["DXVK_ENABLE_NVAPI"] = "0", ["__VK_LAYER_NV_optimus"] = "non_NVIDIA_only",
            ["DXVK_NVAPI_SET_NGX_DEBUG_OPTIONS"] = "DLSSIndicator=1024"
        }};
        var result = InstanceLaunchPlan.Environment(options, defaults);
        Assert.Equal("0", result["DXVK_ENABLE_NVAPI"]);
        Assert.Equal("non_NVIDIA_only", result["__VK_LAYER_NV_optimus"]);
        Assert.Equal("DLSSIndicator=1024", result["DXVK_NVAPI_SET_NGX_DEBUG_OPTIONS"]);
    }

    [Fact]
    public void DriverDiscoveryRequiresX64PeDllAndRespectsInvalidExplicitPath()
    {
        using var dir = new TestDirectory(); var path = Path.Combine(dir.Paths.Data, "driver"); Directory.CreateDirectory(path);
        var file = Path.Combine(path, "_nvngx.dll"); File.WriteAllText(file, "not a driver");
        Assert.Null(RtxRuntimeEnvironment.FindDriverDirectory(null, [path]));
        var bytes = new byte[512];
        using (var writer = new BinaryWriter(new MemoryStream(bytes)))
        {
            writer.Write((ushort)0x5a4d); writer.BaseStream.Position = 0x3c; writer.Write(128);
            writer.BaseStream.Position = 128; writer.Write(0x4550); writer.Write((ushort)0x8664);
            writer.BaseStream.Position = 150; writer.Write((ushort)0x2000); writer.Write((ushort)0x20b);
        }
        File.WriteAllBytes(file, bytes);
        Assert.Equal(path, RtxRuntimeEnvironment.FindDriverDirectory(null, [path]));
        Assert.Null(RtxRuntimeEnvironment.FindDriverDirectory("/nonexistent/ngx", [path]));
        Assert.Null(RtxRuntimeEnvironment.FindDriverDirectory("relative", [path]));
        bytes[132] = 0x4c; bytes[133] = 1; File.WriteAllBytes(file, bytes);
        Assert.Null(RtxRuntimeEnvironment.FindDriverDirectory(null, [path]));
    }
}

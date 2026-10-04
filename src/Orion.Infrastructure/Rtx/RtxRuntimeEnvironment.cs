namespace Orion.Infrastructure.Rtx;

/// <summary>Per-process NVIDIA defaults, shared by both RTX providers. Never installs host drivers.</summary>
public static class RtxRuntimeEnvironment
{
    public static bool SupportsNgxDiscovery(string tag)
    {
        var match = System.Text.RegularExpressions.Regex.Match(tag, @"^(\d+)\.(\d+)-(\d+)-winrt$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success && Version.TryParse($"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}", out var version)
            && version >= new Version(11, 18, 8);
    }

    public static readonly IReadOnlyList<string> DriverDirectories = new[]
    {
        "/usr/lib/nvidia/wine", "/usr/lib64/nvidia/wine",
        "/usr/lib/x86_64-linux-gnu/nvidia/wine", "/usr/lib/x86_64-linux-gnu/nvidia/current/wine"
    };

    public static string? FindDriverDirectory(string? explicitDirectory, IEnumerable<string>? candidates = null)
    {
        // Respect an explicit path, including an invalid one: never mix different driver versions.
        foreach (var directory in explicitDirectory is null ? candidates ?? DriverDirectories : new[] { explicitDirectory })
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            foreach (var name in new[] { "_nvngx.dll", "nvngx.dll" })
            {
                try
                {
                    using var stream = File.OpenRead(Path.Combine(directory, name));
                    using var reader = new BinaryReader(stream);
                    if (stream.Length < 256 || reader.ReadUInt16() != 0x5a4d) continue;
                    stream.Position = 0x3c;
                    var offset = reader.ReadInt32();
                    if (offset < 64 || offset > 1024 * 1024 || offset + 26 > stream.Length) continue;
                    stream.Position = offset;
                    if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664) continue;
                    stream.Position = offset + 22;
                    if ((reader.ReadUInt16() & 0x2000) != 0 && reader.ReadUInt16() == 0x20b) return directory;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
        return null;
    }

    public static bool HasNvidiaDriver(string devices = "/sys/bus/pci/devices")
    {
        try
        {
            return Directory.Exists(devices) && Directory.EnumerateDirectories(devices).Any(device =>
                File.Exists(Path.Combine(device, "vendor")) && File.ReadAllText(Path.Combine(device, "vendor")).Trim() == "0x10de"
                && Path.GetFileName(new DirectoryInfo(Path.Combine(device, "driver")).LinkTarget) == "nvidia");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public static Dictionary<string, string?> Apply(bool active, string? driverDirectory, bool nvidiaDriver,
        IReadOnlyDictionary<string, string?> environment, Action<string>? report = null)
    {
        var result = new Dictionary<string, string?>(environment, StringComparer.Ordinal);
        if (!active) return result;
        // Hide the validation watermark, including the persistent NGX registry indicator.
        // Custom instance variables are applied after these defaults and can opt into diagnostics.
        result["DXVK_NVAPI_SET_NGX_DEBUG_OPTIONS"] = "DLSSIndicator=0";
        result["__NGX_SHOW_INDICATOR"] = "0";
        if (driverDirectory is null)
        {
            report?.Invoke("RTX: NVIDIA NGX driver libraries not found. Ray tracing may still work; automatic DLSS setup is unavailable. No system driver was installed.");
            return result;
        }
        result["NVIDIA_WINE_DLL_DIR"] = driverDirectory;
        result["DXVK_ENABLE_NVAPI"] = "1";
        if (nvidiaDriver)
        {
            result["__NV_PRIME_RENDER_OFFLOAD"] = "1";
            result["__GLX_VENDOR_LIBRARY_NAME"] = "nvidia";
            result["__VK_LAYER_NV_optimus"] = "NVIDIA_only";
        }
        report?.Invoke("RTX: host NGX discovery and NVAPI enabled for this instance; DLSS diagnostic indicator disabled. In-game DLSS still depends on the GPU, driver and game version.");
        return result;
    }
}

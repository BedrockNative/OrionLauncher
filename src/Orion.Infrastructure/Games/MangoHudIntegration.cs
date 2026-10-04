using Orion.Domain;

namespace Orion.Infrastructure.Games;

/// <summary>Enable the system Vulkan layer only for the game launch, never Wine setup or account services.</summary>
public static class MangoHudIntegration
{
    // Upstream Vulkan activation and per-process config contract:
    // https://github.com/flightlessmango/MangoHud#normal-usage
    // https://github.com/flightlessmango/MangoHud#environment-variables
    public static string? FindExecutable(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        foreach (var directory in path.Split(Path.PathSeparator).Where(Path.IsPathFullyQualified))
        {
            try
            {
                var candidate = Path.Combine(directory, "mangohud");
                if (File.Exists(candidate) && (File.GetUnixFileMode(candidate)
                    & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                    return candidate;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }

    public static bool IsAvailable => FindExecutable(Environment.GetEnvironmentVariable("PATH") ?? "") is not null;

    public static Dictionary<string, string?> Apply(InstanceLaunchOptions options,
        IReadOnlyDictionary<string, string?> environment, Action<string>? report = null)
    {
        var result = new Dictionary<string, string?>(environment, StringComparer.Ordinal);
        var hud = options.MangoHud ?? new();
        hud.Validate();
        // Disabled automatic integration leaves explicitly supplied advanced settings alone.
        if (!hud.Enabled) return result;
        var path = result.TryGetValue("PATH", out var configuredPath) ? configuredPath : Environment.GetEnvironmentVariable("PATH");
        if (FindExecutable(path ?? "") is null)
        {
            report?.Invoke("MangoHud was not found in PATH; continuing without automatic overlay.");
            return result;
        }
        // DXVK/VKD3D use Vulkan. No wrapper or LD_PRELOAD is required, so prime-run
        // and literal argument vectors stay intact and native helpers are not hooked.
        result["MANGOHUD"] = "1";
        result["MANGOHUD_CONFIG"] = Configuration(hud);
        report?.Invoke("MangoHud enabled with this instance's metrics (system Vulkan layer). Sensor availability depends on the driver and hardware.");
        return result;
    }

    public static string Configuration(MangoHudOptions hud)
    {
        hud.Validate();
        static string Flag(string key, bool value) => key + "=" + (value ? "1" : "0");
        var position = hud.Position switch
        {
            MangoHudPosition.TopRight => "top-right", MangoHudPosition.BottomLeft => "bottom-left",
            MangoHudPosition.BottomRight => "bottom-right", _ => "top-left"
        };
        // Explicit zeros are necessary: FPS, CPU/GPU and the graph default to on upstream.
        // Do not use read_cfg: global config must not add metrics, limits, logging or commands.
        return string.Join(',', new[]
        {
            Flag("fps", hud.Fps), Flag("frametime", hud.FrameTime), Flag("frame_timing", hud.FrameTime),
            Flag("cpu_stats", hud.Cpu), Flag("gpu_stats", hud.Gpu), Flag("ram", hud.Ram), Flag("vram", hud.Vram),
            Flag("cpu_temp", hud.Cpu && hud.CpuTemperature), Flag("gpu_temp", hud.Gpu && hud.GpuTemperature),
            Flag("battery", hud.Battery), Flag("resolution", hud.Resolution),
            "engine_version=0", "throttling_status=0", "position=" + position,
            Flag("no_display", !(hud.Fps || hud.FrameTime || hud.Cpu || hud.Gpu || hud.Ram || hud.Vram || hud.Battery || hud.Resolution))
        });
    }
}

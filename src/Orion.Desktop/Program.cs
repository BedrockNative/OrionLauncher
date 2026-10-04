using Avalonia;
using Orion.Desktop.Composition;
using Orion.Infrastructure.Linux;
using Orion.Infrastructure.Storage;
using Orion.Infrastructure.Processes;

namespace Orion.Desktop;

internal static class Program
{
    internal static LauncherServices Services { get; private set; } = null!;
    internal static SingleInstance Instance { get; private set; } = null!;
    internal static LaunchRequest Request { get; private set; } = new();

    [STAThread]
    public static int Main(string[] args)
    {
        // No UI, single-instance lock or credential access in the child supervisor.
        if (args.FirstOrDefault() == ProcessSupervisor.Switch)
            return ProcessSupervisor.RunAsync(args[1..]).GetAwaiter().GetResult();
        if (args.Contains("--help"))
        {
            Console.WriteLine("OrionLauncher [--launch INSTANCE-UUID] [--background] | --open brtx://preset/ID | --open /path/preset.rtpack\nLinux Minecraft Bedrock launcher.");
            Console.WriteLine("Developer CurseForge key: CURSEFORGE_API_KEY or ORION_CURSEFORGE_API_KEY_FILE (private file). Restart Orion to apply.");
            return 0;
        }
        if (!OperatingSystem.IsLinux()) { Console.Error.WriteLine("Orion Launcher currently supports Linux only."); return 1; }
        try
        {
            Request = Parse(args);
            Orion.Infrastructure.CurseForge.CurseForgeCredential.CaptureDeveloperOverride();
            var paths = AppPaths.Discover(); paths.EnsureDirectories();
            var instance = SingleInstance.AcquireAsync(paths, Request).GetAwaiter().GetResult();
            if (instance is null) return 0;
            Instance = instance;
            try
            {
                var settings = new SettingsStore(paths).LoadAsync().GetAwaiter().GetResult();
                Services = new(paths, settings, ExecutableCommand(), SupervisorCommand());
                try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
                finally
                {
                    // The UI loop has ended. Also allow cleanup after an unexpected
                    // UI exit without posting new continuations to a stopped dispatcher.
                    SynchronizationContext.SetSynchronizationContext(null);
                    Services.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            finally { Instance.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        catch (LauncherUnresponsiveException ex) { Console.Error.WriteLine($"Orion: {ex.Message}"); return 1; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    internal static LaunchRequest Parse(string[] args)
    {
        Guid? id = null; var background = false; string? link = null; string? file = null;
        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--background": background = true; break;
                case "--launch" when i + 1 < args.Length && Guid.TryParse(args[i + 1], out var parsed): id = parsed; i++; break;
                case "--open" when i + 1 < args.Length:
                    var input = args[++i];
                    if (input.StartsWith("brtx:", StringComparison.OrdinalIgnoreCase)) link = Orion.Domain.RtxLink.Parse(input).ToString();
                    else
                    {
                        file = Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.IsFile && uri.IsLoopback ? uri.LocalPath : input;
                        if (!Path.IsPathFullyQualified(file) || !file.EndsWith(".rtpack", StringComparison.OrdinalIgnoreCase) || file.Any(char.IsControl))
                            throw new ArgumentException("Choose a local .rtpack file or a BetterRTX link.");
                    }
                    break;
                default: throw new ArgumentException($"Unknown or invalid argument: {args[i]}");
            }
        if ((link is not null || file is not null) && (id is not null || background) || link is not null && file is not null)
            throw new ArgumentException("Open RTX content separately from game launch requests.");
        return new(id, background, link, file);
    }

    private static IReadOnlyList<string> ExecutableCommand()
    {
        if (Environment.GetEnvironmentVariable("APPIMAGE") is { } appImage && Path.IsPathFullyQualified(appImage)) return [appImage];
        var portable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../AppRun"));
        if (File.Exists(portable) && Directory.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes"))) return [portable];
        return SupervisorCommand();
    }

    // Shortcuts need the persistent AppImage/AppRun path. Owned supervisors instead
    // reuse the running apphost and inherited bundle environment: invoking the image
    // again would extract the entire payload for every child in extract-and-run mode.
    private static IReadOnlyList<string> SupervisorCommand()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Could not resolve the launcher executable.");
        return Path.GetFileNameWithoutExtension(executable) == "dotnet"
            ? [executable, typeof(Program).Assembly.Location] : [executable];
    }
}

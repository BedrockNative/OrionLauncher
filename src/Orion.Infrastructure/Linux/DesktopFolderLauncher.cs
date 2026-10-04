using System.Diagnostics;
using System.Text;
using Orion.Infrastructure.CurseForge;

namespace Orion.Infrastructure.Linux;

public sealed record FileManagerOption(string Id, string Name, string Executable);

/// <summary>Launch the user's file manager, not a supervised game process or a shell command.</summary>
public sealed class DesktopFolderLauncher(Func<string, string?>? findExecutable = null)
{
    public static IReadOnlyList<FileManagerOption> Options { get; } = [
        new("system", "System default", "xdg-open"),
        new("dolphin", "Dolphin", "dolphin"), new("thunar", "Thunar", "thunar"),
        new("nautilus", "Files (Nautilus)", "nautilus"), new("nemo", "Nemo", "nemo"),
        new("caja", "Caja", "caja"), new("pcmanfm", "PCManFM", "pcmanfm"),
        new("pcmanfm-qt", "PCManFM-Qt", "pcmanfm-qt"),
        new("doublecmd", "Double Commander", "doublecmd")];
    private readonly Func<string, string?> find = findExecutable ?? FindExecutable;

    public bool IsAvailable(string id) => Options.FirstOrDefault(o => o.Id == id) is { } option &&
        (find(option.Executable) is not null || id == "system" && find("gio") is not null);

    public static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            var path = Path.Combine(directory, name);
            try
            {
                if (File.Exists(path) && (File.GetUnixFileMode(path) &
                    (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0) return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public ProcessStartInfo CreateStartInfo(string path, string id)
    {
        var folder = Path.GetFullPath(path);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        var option = Options.FirstOrDefault(o => o.Id == id) ?? throw new ArgumentException("Unknown file manager.");
        // gio delegates to the desktop association without keeping an xdg-open shell around.
        var gio = id == "system" ? find("gio") : null;
        var executable = gio ?? find(option.Executable) ?? throw new FileNotFoundException($"File manager not installed: {option.Name}");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = folder };
        if (gio is not null) info.ArgumentList.Add("open");
        info.ArgumentList.Add(folder);
        CleanEnvironment(info.Environment);
        return info;
    }

    public static void CleanEnvironment(IDictionary<string, string?> environment)
    {
        environment.Remove(CurseForgeCredential.EnvironmentKey);
        environment.Remove(CurseForgeCredential.EnvironmentFile);
        if (!environment.TryGetValue("APPDIR", out var root) || string.IsNullOrWhiteSpace(root)) return;
        root = root.TrimEnd('/') + "/";
        foreach (var key in new[] { "GIO_EXTRA_MODULES", "GTK_PATH", "GDK_PIXBUF_MODULEDIR", "GDK_PIXBUF_MODULE_FILE",
            "FONTCONFIG_FILE", "FONTCONFIG_PATH", "WEBKIT_INJECTED_BUNDLE_PATH", "GST_PLUGIN_SYSTEM_PATH_1_0",
            "GST_PLUGIN_SCANNER_1_0", "ORION_WEBKIT_LIBRARY_DIR" })
        {
            if (environment.TryGetValue(key, out var value) && value is not null &&
                (value.Contains(root, StringComparison.Ordinal) || key is "GDK_PIXBUF_MODULE_FILE" or "ORION_WEBKIT_LIBRARY_DIR"))
                environment.Remove(key);
        }
        foreach (var key in new[] { "PATH", "XDG_DATA_DIRS", "LD_LIBRARY_PATH" })
            if (environment.TryGetValue(key, out var value) && value is not null)
            {
                var cleaned = string.Join(':', value.Split(':').Where(p => !p.StartsWith(root, StringComparison.Ordinal) && p + "/" != root));
                if (cleaned.Length == 0) environment.Remove(key); else environment[key] = cleaned;
            }
        environment.Remove("APPDIR"); environment.Remove("APPIMAGE");
    }

    public async Task OpenAsync(string path, string id, Action<Exception>? lateError = null)
    {
        var info = CreateStartInfo(path, id);
        var process = Process.Start(info) ?? throw new IOException("Could not start the file manager.");
        // Keep pipes alive and drain them concurrently. Some managers remain running until
        // their window closes; never block the UI or kill those user-owned windows.
        var completion = ObserveAsync(process);
        if (await Task.WhenAny(completion, Task.Delay(750)) == completion) await completion;
        else _ = ReportLaterAsync(completion, lateError);
    }

    private static async Task ObserveAsync(Process process)
    {
        using (process)
        {
            async Task<string> Drain(StreamReader reader)
            {
                var captured = new StringBuilder(); var buffer = new char[2048]; int count;
                while ((count = await reader.ReadAsync(buffer)) > 0)
                    if (captured.Length < 4096) captured.Append(buffer, 0, Math.Min(count, 4096 - captured.Length));
                return captured.ToString();
            }
            var output = Drain(process.StandardOutput); var error = Drain(process.StandardError);
            await Task.WhenAll(process.WaitForExitAsync(), output, error);
            if (process.ExitCode != 0) throw new IOException($"File manager exited with code {process.ExitCode}: {(await error).Trim()}");
        }
    }

    private static async Task ReportLaterAsync(Task completion, Action<Exception>? report)
    {
        try { await completion; }
        catch (Exception ex) { if (report is not null) report(ex); else Console.Error.WriteLine("Open folder: " + ex.Message); }
    }
}

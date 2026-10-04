using System.Diagnostics;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Linux;

/// <summary>Opt-in per-user handler discovery; never takes over an existing default handler.</summary>
public sealed class RtxProtocolIntegration(AppPaths paths, IReadOnlyList<string> command, string icon)
{
    private const string Marker = "X-Orion-RTX-Managed=true";
    private string Entry => Path.Combine(paths.Applications, "io.bedrocknative.orion-rtx.desktop");
    public bool Registered => File.Exists(Entry) && new FileInfo(Entry).LinkTarget is null && File.ReadAllLines(Entry).Contains(Marker);
    public async Task SetEnabledAsync(bool enabled)
    {
        if (new FileInfo(Entry).LinkTarget is not null || File.Exists(Entry) && !Registered)
            throw new IOException("Refusing to replace an unmanaged BetterRTX desktop entry.");
        if (enabled)
        {
            Directory.CreateDirectory(paths.Applications);
            await AtomicFile.WriteAsync(Entry, Render(command, icon), CancellationToken.None);
        }
        else if (Registered) File.Delete(Entry);
        var tool = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Where(Path.IsPathFullyQualified)
            .Select(p => Path.Combine(p, "update-desktop-database")).FirstOrDefault(File.Exists);
        if (tool is not null)
        {
            var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(paths.Applications);
            using var process = Process.Start(start) ?? throw new IOException("Cannot refresh desktop handlers.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); throw new IOException("Desktop handler cache refresh timed out."); }
            await stdout; var error = await stderr;
            if (process.ExitCode != 0) throw new IOException("Desktop handler cache could not be refreshed: " + error);
        }
    }
    public static string Render(IReadOnlyList<string> command, string icon) =>
        "[Desktop Entry]\nType=Application\nName=Orion RTX Studio\nNoDisplay=true\nTerminal=false\n" +
        $"Exec={string.Join(' ', command.Select(DesktopIntegration.Argument))} --open %u\n" +
        $"Icon={icon.Replace("\n", "").Replace("\r", "")}\nMimeType=x-scheme-handler/brtx;\n{Marker}\n";
}

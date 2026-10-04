using System.Text;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Games;

/// <summary>Session-scoped collection from known game log locations, never a recursive prefix scan.</summary>
public sealed class GameSessionDiagnostics : IAsyncDisposable
{
    private readonly string root;
    private readonly string journal;
    private readonly Dictionary<string, Cursor> cursors = new(StringComparer.Ordinal);
    private readonly HashSet<string> warnings = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stop = new();
    private Task? polling;
    private long collected;
    private int unhandledException;
    private bool disposed;
    private const long SessionLimit = 8 * 1024 * 1024;
    private sealed class Cursor(long offset, DateTime modified)
    {
        public long Offset = offset;
        public DateTime Modified = modified;
        public Decoder Decoder = Encoding.UTF8.GetDecoder();
    }
    public bool HasUnhandledException => Volatile.Read(ref unhandledException) != 0;

    public GameSessionDiagnostics(string instanceRoot, string log)
    {
        root = ContentFiles.Safe(instanceRoot); journal = log;
        // Skip history from previous launches; report only changes in this session.
        foreach (var file in Files())
            Try(file, () => { var info = new FileInfo(file); cursors[file] = new(info.Length, info.LastWriteTimeUtc); });
    }

    public static void ConfigureEnvironment(Dictionary<string, string?> environment)
    {
        environment["WINEDEBUG"] = "-all,err+all";
        environment["DXVK_LOG_PATH"] = "none"; // DXVK continues to write stderr.
        environment["VKD3D_LOG_FILE"] = null; // VKD3D defaults to stderr.
    }

    public void ObserveOutput(string text)
    {
        if (text.Contains("wine: Unhandled page fault", StringComparison.OrdinalIgnoreCase)
            || text.Contains("wine: Unhandled exception", StringComparison.OrdinalIgnoreCase))
            Interlocked.Exchange(ref unhandledException, 1);
    }

    public void Start()
    {
        if (polling is not null || disposed) throw new InvalidOperationException("Diagnostics already started or disposed.");
        polling = PollAsync();
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try { while (await timer.WaitForNextTickAsync(stop.Token)) Collect(); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public void Collect()
    {
        foreach (var file in Files())
            Try(file, () => CollectFile(file));
    }

    private void CollectFile(string file)
    {
        var info = new FileInfo(file);
        if (!cursors.TryGetValue(file, out var cursor))
        {
            if (cursors.Count >= 512) return;
            cursors[file] = cursor = new(0, DateTime.MinValue);
        }
        if (info.Length == cursor.Offset && info.LastWriteTimeUtc == cursor.Modified) return;
        if (file.EndsWith(".crashedsession", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
        {
            // Crash markers may contain device/account identifiers; dumps contain
            // arbitrary memory. Reference them, never paste their contents.
            ProcessLog.Event(journal, "Minecraft report", $"Updated report: {Path.GetRelativePath(root, file)} ({info.Length} bytes). Preserved on disk; not a stack trace.");
            cursor.Offset = info.Length; cursor.Modified = info.LastWriteTimeUtc;
            return;
        }
        if (info.Length <= cursor.Offset) { cursor.Offset = 0; cursor.Decoder.Reset(); }
        if (collected >= SessionLimit)
        {
            Warn("limit", "Supplemental log collection reached 8 MiB for this session. Original files remain on disk."); return;
        }
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Position = cursor.Offset;
        var bytes = new byte[(int)Math.Min(64 * 1024, SessionLimit - collected)];
        var count = stream.Read(bytes);
        if (count == 0) return;
        var chars = new char[Encoding.UTF8.GetMaxCharCount(count)];
        var length = cursor.Decoder.GetChars(bytes, 0, count, chars, 0, false);
        ProcessLog.Event(journal, $"Minecraft · {Path.GetFileName(file)}", new string(chars, 0, length));
        cursor.Offset = stream.Position; cursor.Modified = info.LastWriteTimeUtc; collected += count;
    }

    public async Task EnableContentLogsAsync(CancellationToken ct)
    {
        var enabled = 0;
        foreach (var directory in Editions())
        foreach (var profile in Directories(Path.Combine(directory, "Users")))
        {
            var path = Path.Combine(profile, "games/com.mojang/minecraftpe/options.txt");
            if (!Safe(path) || !File.Exists(path)) continue;
            try
            {
                var source = ContentFiles.ReadSmall(path);
                var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                var lines = source.Replace("\r\n", "\n").Split('\n').ToList();
                lines.RemoveAll(l => l.StartsWith("content_log_file:", StringComparison.Ordinal));
                while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
                lines.Add("content_log_file:1");
                var updated = string.Join(newline, lines) + newline;
                if (updated != source) await AtomicFile.WriteAsync(path, updated, ct);
                enabled++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { Warn(path, "Could not enable Minecraft's content log for an existing storage profile."); }
        }
        ProcessLog.Event(journal, "Diagnostics", enabled > 0
            ? "Minecraft content logs enabled; new content/errors and crash-report locations are included in this journal."
            : "No Minecraft options file yet. Content logging will be enabled on the next launch after a storage profile is created.");
    }

    private IEnumerable<string> Editions()
    {
        foreach (var user in Directories(Path.Combine(root, "prefix/drive_c/users")))
        foreach (var edition in new[] { "Minecraft Bedrock", "Minecraft Bedrock Preview" })
        {
            var path = Path.Combine(user, "AppData/Roaming", edition);
            if (Safe(path) && Directory.Exists(path)) yield return path;
        }
    }

    private IEnumerable<string> Files()
    {
        foreach (var edition in Editions())
        {
            foreach (var file in LogFiles(Path.Combine(edition, "logs"))) yield return file;
            foreach (var file in LogFiles(Path.Combine(edition, "bootstrapStorage/crash"))) yield return file;
            foreach (var profile in Directories(Path.Combine(edition, "Users")))
            {
                var file = Path.Combine(profile, "games/com.mojang/minecraftpe/NonAssertErrorLog.txt");
                if (Safe(file) && File.Exists(file)) yield return file;
            }
        }
        foreach (var user in Directories(Path.Combine(root, "prefix/drive_c/users")))
        foreach (var package in new[] { "Microsoft.MinecraftUWP_8wekyb3d8bbwe", "Microsoft.MinecraftWindowsBeta_8wekyb3d8bbwe" })
        {
            var state = Path.Combine(user, "AppData/Local/Packages", package, "LocalState");
            foreach (var file in LogFiles(Path.Combine(state, "logs"))) yield return file;
            foreach (var file in LogFiles(Path.Combine(state, "bootstrapStorage/crash"))) yield return file;
        }
    }

    private IEnumerable<string> LogFiles(string directory) => Entries(directory, false).Where(f =>
        Path.GetExtension(f).ToLowerInvariant() is ".log" or ".txt" or ".crashedsession" or ".dmp");
    private IEnumerable<string> Directories(string directory) => Entries(directory, true);
    private string[] Entries(string path, bool directories)
    {
        if (!Safe(path) || !Directory.Exists(path)) return [];
        try
        {
            return (directories ? Directory.EnumerateDirectories(path) : Directory.EnumerateFiles(path))
                .Take(128).Where(Safe).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn(path, "An instance diagnostic directory is unreadable."); return []; }
    }
    private bool Safe(string path)
    {
        try { ContentFiles.Safe(root, Path.GetRelativePath(root, path)); return true; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }
    private void Try(string path, Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn(path, $"Could not read supplemental log: {Path.GetFileName(path)}"); }
    }
    private void Warn(string key, string message)
    {
        if (warnings.Add(key)) ProcessLog.Event(journal, "Diagnostics", message);
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        await stop.CancelAsync();
        if (polling is not null) await polling;
        // Drain the bounded remainder after Wine and its debugger have exited.
        for (var i = 0; i < 128; i++) { var previous = collected; Collect(); if (previous == collected) break; }
        stop.Dispose();
    }
}

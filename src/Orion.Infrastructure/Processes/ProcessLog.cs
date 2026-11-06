using System.Collections.Concurrent;
using System.Text;

namespace Orion.Infrastructure.Processes;

/// <summary>Serializes appenders sharing a journal, including service and child output.</summary>
public static class ProcessLog
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.Ordinal);

    public static void Clear(string path)
    {
        path = Path.GetFullPath(path);
        lock (Gates.GetOrAdd(path, _ => new()))
        {
            Content.ContentFiles.Safe(Path.GetDirectoryName(path)!, Path.GetFileName(path));
            if (!File.Exists(path)) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            stream.SetLength(0);
        }
    }

    // Only Orion's top-level journals. Never traverse game data or crash dumps.
    public static int ClearJournals(string directory)
    {
        Content.ContentFiles.Safe(directory);
        if (!Directory.Exists(directory)) return 0;
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
        {
            Clear(path);
            count++;
        }
        return count;
    }

    public static void Append(string path, string text)
    {
        lock (Gates.GetOrAdd(Path.GetFullPath(path), _ => new()))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(text);
        }
    }

    public static void Event(string path, string source, string text) =>
        Append(path, $"\n[{DateTimeOffset.Now:HH:mm:ss}] [{source}] {text}\n");

    private static readonly ConcurrentDictionary<string, (string Key, long Start, long End)> LastProgress = new(StringComparer.Ordinal);

    /// <summary>Rewrites the previous event in place while it is still the journal's last write for <paramref name="key"/>.</summary>
    public static void Progress(string path, string source, string key, string text)
    {
        var full = Path.GetFullPath(path);
        lock (Gates.GetOrAdd(full, _ => new()))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var stream = new FileStream(full, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            var start = LastProgress.TryGetValue(full, out var last) && last.Key == key && last.End == stream.Length ? last.Start : stream.Length;
            stream.SetLength(start);
            stream.Position = start;
            stream.Write(new UTF8Encoding(false).GetBytes($"\n[{DateTimeOffset.Now:HH:mm:ss}] [{source}] {text}\n"));
            LastProgress[full] = (key, start, stream.Length);
        }
    }
}

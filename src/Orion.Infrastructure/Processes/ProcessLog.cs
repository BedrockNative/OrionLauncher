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
}

using System.Security.Cryptography;
using System.Text.Json;
using Orion.Infrastructure.Content;

namespace Orion.Infrastructure.Rtx;

/// <summary>Durable rollback journal. A process crash rolls back before the next edit or game launch.</summary>
internal static class RtxTransaction
{
    private sealed record Change(string Path, string? Before, string After);
    private static string Journal(string root) => ContentFiles.Safe(root, "rtx/transaction");
    internal static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    internal static string? HashFile(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length > 128 * 1024 * 1024) throw new InvalidDataException("Oversized RTX transaction file.");
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static string Target(string root, string relative)
    {
        if (relative != "rtx/current.json" && relative != "rtx/configuration.json" && relative != "rtx/cleanup.json"
            && relative != "rtx/betterrtx/configuration.json" && relative != "rtx/vanillartx/configuration.json"
            && relative != "rtx/dlss/current.json" && relative != "rtx/dlss/original.dll"
            && !(relative.StartsWith("rtx/dlss/quarantine-", StringComparison.Ordinal) && relative.EndsWith(".dll", StringComparison.Ordinal) && Guid.TryParseExact(relative[20..^4], "N", out _))
            && !(relative.StartsWith("game/", StringComparison.Ordinal) && relative.EndsWith("/nvngx_dlss.dll", StringComparison.Ordinal))
            && !(relative.StartsWith("game/", StringComparison.Ordinal) && relative.Contains("/data/renderer/materials/", StringComparison.Ordinal)
                && (relative.EndsWith("/materials.index.json", StringComparison.Ordinal) || relative.EndsWith(".material.bin", StringComparison.Ordinal)))
            && !(relative.StartsWith("prefix/drive_c/users/", StringComparison.Ordinal) && relative.EndsWith("/games/com.mojang/minecraftpe/options.txt", StringComparison.Ordinal)))
            throw new InvalidDataException("Unexpected RTX transaction target.");
        return ContentFiles.Safe(root, relative);
    }
    internal static void Recover(string root)
    {
        var journal = Journal(root);
        if (!Directory.Exists(journal)) return;
        if (File.Exists(ContentFiles.Safe(journal, "changes.json")) && !File.Exists(ContentFiles.Safe(journal, "committed")))
        {
            var changes = JsonSerializer.Deserialize<Change[]>(ContentFiles.ReadSmall(Path.Combine(journal, "changes.json")))
                ?? throw new InvalidDataException("Invalid RTX recovery journal.");
            if (changes.Length > 256) throw new InvalidDataException("Oversized RTX journal.");
            // Verify everything first; never overwrite external edits or trust damaged backup bytes.
            for (var i = 0; i < changes.Length; i++)
            {
                var c = changes[i]; var current = HashFile(Target(root, c.Path));
                if (current != c.Before && current != c.After) throw new IOException("RTX recovery found externally modified files. Keep the rtx/transaction folder for recovery.");
                if (c.Before is not null && HashFile(ContentFiles.Safe(journal, i + ".before")) != c.Before)
                    throw new IOException("RTX rollback backup is damaged.");
            }
            for (var i = changes.Length - 1; i >= 0; i--)
            {
                var c = changes[i]; var target = Target(root, c.Path);
                if (c.Before is null)
                {
                    if (File.Exists(target)) File.Delete(target);
                    var parent = Path.GetDirectoryName(target)!; var name = Path.GetFileName(parent);
                    if (name.StartsWith("orion-rtx-", StringComparison.Ordinal) && Guid.TryParseExact(name[10..], "N", out _)
                        && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
                }
                else
                {
                    var restore = ContentFiles.Safe(journal, i + ".restore");
                    File.Copy(Path.Combine(journal, i + ".before"), restore, true);
                    File.Move(restore, target, true);
                }
            }
        }
        ContentFiles.DeleteWork(journal);
    }

    internal static void Apply(string root, IReadOnlyDictionary<string, byte[]> writes, CancellationToken ct)
    {
        Recover(root);
        var journal = Journal(root); Directory.CreateDirectory(journal);
        try
        {
            List<Change> changes = [];
            foreach (var (relative, bytes) in writes)
            {
                ct.ThrowIfCancellationRequested(); var target = Target(root, relative);
                if (bytes.Length > 128 * 1024 * 1024 || File.Exists(target) && new FileInfo(target).Length > 128 * 1024 * 1024) throw new InvalidDataException("Oversized RTX transaction file.");
                var before = File.Exists(target) ? File.ReadAllBytes(target) : null;
                if (before is not null) Durable(Path.Combine(journal, changes.Count + ".before"), before);
                Durable(Path.Combine(journal, changes.Count + ".after"), bytes);
                changes.Add(new(relative, before is null ? null : Hash(before), Hash(bytes)));
            }
            Replace(Path.Combine(journal, "changes.json"), JsonSerializer.SerializeToUtf8Bytes(changes));
            for (var i = 0; i < changes.Count; i++)
            {
                ct.ThrowIfCancellationRequested(); var target = Target(root, changes[i].Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(Path.Combine(journal, i + ".after"), target, true);
            }
            ct.ThrowIfCancellationRequested();
            Durable(Path.Combine(journal, "committed"), [1]);
        }
        finally { Recover(root); }
    }
    private static void Durable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
    }
    private static void Replace(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { Durable(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

using System.IO.Compression;

namespace Orion.Infrastructure.Content;

internal static class ContentFiles
{
    internal const long MaximumBytes = 8L * 1024 * 1024 * 1024;
    internal const int MaximumFiles = 100_000;
    internal sealed class Budget(long maximumBytes = MaximumBytes, int maximumFiles = MaximumFiles)
    {
        public long Limit { get; } = maximumBytes;
        public long Bytes;
        public int Files;
        public void Add(long bytes)
        {
            Bytes = checked(Bytes + bytes);
            if (Bytes > Limit) throw new InvalidDataException("Content exceeds its extraction size limit.");
        }
        public void File()
        {
            if (++Files > maximumFiles) throw new InvalidDataException("Content has too many files.");
        }
    }

    // Wine prefixes contain intentional host links. Content operations must never follow them.
    internal static string Safe(string root, string relative = "")
    {
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (full != fullRoot && !full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Content path is outside this instance.");
        var current = Path.GetPathRoot(full)!;
        foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new InvalidDataException("Symbolic links are not supported in game content.");
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked game content is not supported.");
        }
        return full;
    }

    internal static IEnumerable<string> Walk(string root, CancellationToken ct, int depth = 0)
    {
        if (depth > 40) throw new InvalidDataException("Content directories are nested too deeply.");
        foreach (var path in Directory.EnumerateFileSystemEntries(Safe(root)))
        {
            ct.ThrowIfCancellationRequested(); Safe(root, Path.GetRelativePath(root, path));
            if (Directory.Exists(path))
                foreach (var file in Walk(path, ct, depth + 1)) yield return file;
            else yield return path;
        }
    }

    internal static void Extract(string archive, string target, Budget budget, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        using var zip = ZipFile.OpenRead(archive);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var components = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested(); budget.File();
            // Some Bedrock publishers package on Windows with backslashes. Normalize
            // BEFORE validation so mixed-separator traversal and collisions are still rejected.
            var name = entry.FullName.Replace('\\', '/');
            while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            if (name.Length == 0 && entry.Length == 0) continue; // optional archive-root directory
            var parts = name.TrimEnd('/').Split('/');
            if (name.Length == 0 || parts.Length > 40 || parts.Any(p => p is "" or "." or ".." || p.Any(c => char.IsControl(c) || ":*?\"<>|".Contains(c)) || p.EndsWith(' ') || p.EndsWith('.'))
                || !names.Add(name.TrimEnd('/')) || ((entry.ExternalAttributes >> 16) & 0xf000) is not (0 or 0x8000 or 0x4000))
                throw new InvalidDataException("The archive contains unsafe or duplicate paths.");
            for (var i = 0; i < parts.Length; i++)
            {
                var component = string.Join('/', parts.Take(i + 1));
                if (components.TryGetValue(component, out var previous) && previous != component)
                    throw new InvalidDataException("Archive paths differ only by letter case.");
                components[component] = component;
                var stem = parts[i].Split('.')[0].ToUpperInvariant();
                if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))
                    throw new InvalidDataException("Archive contains a reserved Windows path.");
            }
            var destination = Safe(target, name);
            if (name.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
            if (entry.Length > budget.Limit - budget.Bytes) throw new InvalidDataException("Content exceeds its extraction size limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            Copy(input, output, budget, ct);
        }
    }

    internal static void Copy(Stream input, Stream output, Budget budget, CancellationToken ct)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) != 0)
        { ct.ThrowIfCancellationRequested(); budget.Add(read); output.Write(buffer, 0, read); }
    }

    internal static string ReadSmall(string path, int limit = 1024 * 1024)
    {
        Safe(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        using var stream = File.OpenRead(path);
        if (stream.Length > limit) throw new InvalidDataException("Content metadata is too large.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static void DeleteWork(string root)
    {
        // Validate the entire tree first. Never recursively delete an unvalidated link.
        _ = Walk(root, CancellationToken.None).ToArray();
        Directory.Delete(root, true);
    }
}

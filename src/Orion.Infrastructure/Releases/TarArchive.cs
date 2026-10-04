using System.Formats.Tar;
using System.IO.Compression;

namespace Orion.Infrastructure.Releases;

/// <summary>Extracts files first and links last, so entries cannot write through archive-created symlinks.</summary>
public static class TarArchive
{
    public static async Task ExtractAsync(string archive, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        if (Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("Archive destination must be empty.");
        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip);
        var links = new List<(string Path, string Target, bool Hard)>();
        long total = 0;
        int entries = 0;
        while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            var sparse = SparseTarFile.Describe(entry);
            if (++entries > 250_000 || (total += sparse?.Length ?? entry.Length) > 12L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Runtime archive exceeds extraction limits.");
            var target = ContainedPath(destination, sparse?.Name ?? entry.Name);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                    {
                        if (sparse is not null) await SparseTarFile.ExtractAsync(entry.DataStream!, output, sparse.Length, ct);
                        else if (entry.DataStream is { } data) await data.CopyToAsync(output, ct);
                    }
                    File.SetUnixFileMode(target, entry.Mode & (UnixFileMode)0x1FF);
                    break;
                case TarEntryType.SymbolicLink:
                case TarEntryType.HardLink:
                    if (Path.IsPathRooted(entry.LinkName)) throw new InvalidDataException("Absolute archive link.");
                    var hard = entry.EntryType == TarEntryType.HardLink;
                    var linkTarget = ContainedPath(destination, Path.GetRelativePath(destination,
                        Path.GetFullPath(entry.LinkName, hard ? destination : Path.GetDirectoryName(target)!)));
                    links.Add((target, linkTarget, hard));
                    break;
                default: throw new InvalidDataException($"Unsupported archive entry: {entry.EntryType}");
            }
        }
        // A link must not be an ancestor of another link or file. Chains remain inside the extraction root.
        foreach (var link in links)
        {
            if (links.Any(other => other.Path != link.Path && link.Path.StartsWith(other.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw new InvalidDataException("Archive link has a link parent.");
            Directory.CreateDirectory(Path.GetDirectoryName(link.Path)!);
            if (link.Hard) File.Copy(link.Target, link.Path, false);
            else File.CreateSymbolicLink(link.Path, Path.GetRelativePath(Path.GetDirectoryName(link.Path)!, link.Target));
        }
    }

    public static string ContainedPath(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Absolute archive path.");
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (path != fullRoot && !path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Archive path escapes its destination.");
        return path;
    }
}

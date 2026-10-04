using System.Formats.Tar;
using System.Globalization;

namespace Orion.Infrastructure.Releases;

/// <summary>GNU PAX sparse format 1.0, used by the published Xodus binaries.</summary>
internal sealed record SparseTarFile(string Name, long Length)
{
    public static SparseTarFile? Describe(TarEntry entry)
    {
        if (entry is not PaxTarEntry pax || !pax.ExtendedAttributes.Keys.Any(k => k.StartsWith("GNU.sparse.", StringComparison.Ordinal))) return null;
        var attributes = pax.ExtendedAttributes;
        if (entry.EntryType != TarEntryType.RegularFile || attributes.GetValueOrDefault("GNU.sparse.major") != "1" || attributes.GetValueOrDefault("GNU.sparse.minor") != "0"
            || !attributes.TryGetValue("GNU.sparse.name", out var name)
            || !long.TryParse(attributes.GetValueOrDefault("GNU.sparse.realsize"), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            || length > 12L * 1024 * 1024 * 1024 || entry.DataStream is null)
            throw new InvalidDataException("Unsupported or invalid GNU sparse archive entry.");
        return new(name, length);
    }

    public static async Task ExtractAsync(Stream input, FileStream output, long length, CancellationToken ct)
    {
        var consumed = 0;
        var single = new byte[1];
        async Task<long> Number()
        {
            long value = 0;
            for (var digits = 0; digits <= 19; digits++)
            {
                await input.ReadExactlyAsync(single, ct); consumed++;
                if (single[0] == '\n' && digits > 0) return value;
                if (single[0] < '0' || single[0] > '9' || digits == 19) throw new InvalidDataException("Invalid sparse file map.");
                try { value = checked(value * 10 + single[0] - '0'); }
                catch (OverflowException ex) { throw new InvalidDataException("Sparse file map overflow.", ex); }
            }
            throw new InvalidDataException("Invalid sparse file map.");
        }
        var count = await Number();
        if (count > 100_000) throw new InvalidDataException("Sparse file has too many extents.");
        var extents = new List<(long Offset, long Length)>();
        long end = 0;
        for (long i = 0; i < count; i++)
        {
            var offset = await Number(); var size = await Number();
            if (offset < end || offset > length || size > length - offset) throw new InvalidDataException("Sparse extent exceeds the file.");
            extents.Add((offset, size)); end = offset + size;
        }
        var padding = (512 - consumed % 512) % 512;
        if (padding != 0) await input.ReadExactlyAsync(new byte[padding], ct);
        output.SetLength(length);
        var buffer = new byte[81920];
        foreach (var extent in extents)
        {
            output.Position = extent.Offset;
            var remaining = extent.Length;
            while (remaining > 0)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ct);
                if (read == 0) throw new InvalidDataException("Truncated sparse file.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                remaining -= read;
            }
        }
        if (await input.ReadAsync(single, ct) != 0) throw new InvalidDataException("Unexpected trailing sparse data.");
    }
}

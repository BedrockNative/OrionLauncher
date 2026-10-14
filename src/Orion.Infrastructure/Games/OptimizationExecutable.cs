using Orion.Infrastructure.Storage;
using Orion.Infrastructure.Content;

namespace Orion.Infrastructure.Games;

public static class OptimizationExecutable
{
    private sealed record Stamp(string Source, long Length, long Modified, long CopyLength);
    public static async Task<string> PrepareAsync(string executable, string dll, CancellationToken ct, Func<CancellationToken, Task<byte[]>>? readSource = null)
    {
        executable = ContentFiles.Safe(executable);
        var directory = Path.GetDirectoryName(executable)!;
        var target = ContentFiles.Safe(directory, Path.GetFileNameWithoutExtension(executable) + ".Orion.exe");
        var metadata = ContentFiles.Safe(directory, ".orion-optimization.json");
        var localDll = ContentFiles.Safe(directory, "Orion.Optimization.dll");
        var info = new FileInfo(executable);
        var stamp = await AtomicFile.ReadJsonAsync<Stamp>(metadata, ct);
        // The DLL name is stable: updating the mod does not rebuild the executable.
        var installedDll = new FileInfo(localDll); var sourceDll = new FileInfo(dll);
        if (!installedDll.Exists || installedDll.Length != sourceDll.Length || installedDll.LastWriteTimeUtc != sourceDll.LastWriteTimeUtc)
        {
            var temporary = localDll + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.Copy(dll, temporary); ct.ThrowIfCancellationRequested(); File.Move(temporary, localDll, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (stamp is not null && stamp.Source == executable && stamp.Length == info.Length
            && stamp.Modified == info.LastWriteTimeUtc.Ticks && File.Exists(target) && new FileInfo(target).Length == stamp.CopyLength)
            return target;
        if (info.Length > 1024L * 1024 * 1024) throw new InvalidDataException("Game executable is too large.");
        var bytes = readSource is null ? await File.ReadAllBytesAsync(executable, ct) : await readSource(ct);
        var patched = OptimizationImport.Add(bytes, "Orion.Optimization.dll");
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, patched, ct);
            ct.ThrowIfCancellationRequested(); File.Move(temp, target, true);
            await AtomicFile.WriteJsonAsync(metadata, new Stamp(executable, info.Length, info.LastWriteTimeUtc.Ticks, patched.LongLength), ct);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return target;
    }
}

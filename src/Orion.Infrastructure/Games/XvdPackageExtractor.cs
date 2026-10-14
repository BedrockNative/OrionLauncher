using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using DiscUtils;
using DiscUtils.Ntfs;
using DiscUtils.Partitions;
using LibXboxOne;
using LibXboxOne.Keys;
using Orion.Domain;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Runtime;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Games;

public sealed class XvdPackageExtractor(AppPaths paths, ProcessRunner runner, ContentKeyCache cache)
{
    private static readonly SemaphoreSlim libraryGate = new(1);
    public static void ClearLibraryKeys()
    {
        libraryGate.Wait();
        try { foreach (var key in DurangoKeys.GetAllCIK()) if (key.Value.KeyData is { } bytes) CryptographicOperations.ZeroMemory(bytes); }
        finally { libraryGate.Release(); }
    }
    public const string Marker = ".orion-decrypted";

    public async Task ExtractAsync(string package, string destination, string cli, string account,
        IReadOnlyDictionary<string, string?> environment, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        byte[] header = new byte[0x230];
        await using (var stream = File.OpenRead(package)) await stream.ReadExactlyAsync(header, ct);
        if (!header.AsSpan(0x200, 8).SequenceEqual("msft-xvd"u8)) throw new InvalidDataException("Not an XVD package.");
        var content = new Guid(header.AsSpan(0x220, 16));
        var encrypted = (header[0x208] & 2) == 0;
        var keys = encrypted ? await cache.GetAsync(account, content, async token =>
        {
            var folder = Path.Combine(paths.Runtime, "orion-keys-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var result = new List<byte[]>();
            try
            {
                await runner.CaptureAsync(new(cli, ["license", content.ToString(), folder], folder, environment), token,
                    "Xodus could not obtain this package's content license.");
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(file).Length != 48
                        || !Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id) || Path.GetExtension(file) != ".cik")
                        throw new InvalidDataException("Xodus returned an invalid content key file.");
                    var bytes = await File.ReadAllBytesAsync(file, token);
                    result.Add(bytes);
                    if (new Guid(bytes.AsSpan(0, 16)) != id) throw new InvalidDataException("Content key identity mismatch.");
                }
                return result.ToArray();
            }
            catch { foreach (var key in result) CryptographicOperations.ZeroMemory(key); throw; }
            finally { Directory.Delete(folder, true); }
        }, ct) : [];
        try
        {
            await libraryGate.WaitAsync(ct);
            try { await Task.Run(() => ExtractCore(package, destination, keys, progress, ct), ct); }
            finally { libraryGate.Release(); }
        }
        finally { foreach (var key in keys) CryptographicOperations.ZeroMemory(key); }
    }

    private static void ExtractCore(string package, string destination, byte[][] keys, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var working = Path.Combine(Path.GetDirectoryName(destination)!, ".decrypt-" + Guid.NewGuid().ToString("N") + ".msixvc");
        try
        {
            progress?.Report(new("Preparing full package decryption"));
            using (var input = File.OpenRead(package))
            using (var output = new FileStream(working, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
                Copy(input, output, ct);
            using var quiet = LibraryOutput.Enter(ct);
            foreach (var key in keys)
            {
                DurangoKeys.LoadCikFromBytes(key);
                // Upstream retains key entries by hash even after we clear their bytes.
                key.AsSpan(16, 32).CopyTo(DurangoKeys.GetCikByGuid(new Guid(key.AsSpan(0, 16))).KeyData);
            }
            using var xvd = new XvdFile(working);
            if (!xvd.Load()) throw new InvalidDataException("XvdTool could not read the package.");
            var blocks = xvd.IsEncrypted ? (xvd.IsXvcFile
                ? xvd.RegionHeaders.Where(h => h.Length > 0 && h.Offset > 0 && h.KeyId != LibXboxOne.XvcConstants.XVC_KEY_NONE && h.KeyId + 1 <= xvd.XvcInfo.KeyCount)
                    .Sum(h => checked((long)XvdMath.BytesToPages(h.Length)))
                : checked((long)XvdMath.BytesToPages((ulong)new FileInfo(working).Length - xvd.UserDataOffset))) : 0;
            var decryptProgress = new PackagePhaseProgress("Decrypting blocks", blocks, progress);
            long started = 0;
            // The pinned upstream emits one JSON progress line before each 4-KiB page.
            quiet.OnProgress = () => decryptProgress.Report(Math.Max(0, started++));
            var previousSave = XvdFile.DisableSaveAfterModification;
            try
            {
                // We read the decrypted image immediately using its in-memory header.
                // Rehashing/saving a temporary image that is deleted afterwards adds no value.
                XvdFile.DisableSaveAfterModification = true;
                if (xvd.IsEncrypted && !xvd.Decrypt() || xvd.IsEncrypted)
                    throw new InvalidDataException("XvdTool could not decrypt the complete package.");
                decryptProgress.Complete();
            }
            finally { quiet.OnProgress = null; XvdFile.DisableSaveAfterModification = previousSave; }
            ct.ThrowIfCancellationRequested();
            progress?.Report(new("Extracting the decrypted filesystem"));
            using var disk = new XvdFilesystemStream(xvd);
            var geometry = Geometry.FromCapacity(checked((long)xvd.Header.DriveSize), (int)xvd.Header.SectorSize);
            PartitionTable table;
            try { table = new GuidPartitionTable(disk, geometry); }
            catch (Exception ex) when (ex is IOException or ArgumentException) { disk.Position = 0; table = new BiosPartitionTable(disk, geometry); }
            if (table.Count == 0) { disk.Position = 0; table = new BiosPartitionTable(disk, geometry); }
            if (table.Count == 0) throw new InvalidDataException("No package filesystem found.");
            using var partition = table.Partitions[0].Open();
            // DiscUtils marks its NTFS API Windows-only because write/security APIs
            // use Windows identities; this read-only stream path is used by XvdTool on Linux.
#pragma warning disable CA1416
            using var fs = new NtfsFileSystem(partition);
#pragma warning restore CA1416
            ExtractFilesystem(fs, destination, progress, ct);
            File.WriteAllText(Path.Combine(destination, Marker), "1\n");
            GameLayout.Validate(destination);
        }
        finally
        {
            foreach (var key in DurangoKeys.GetAllCIK())
                if (key.Value.KeyData is { } bytes) CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(working)) File.Delete(working);
        }
    }

    public static void ExtractFilesystem(DiscFileSystem fs, string destination, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new("Scanning files for extraction"));
        var files = new List<(DiscFileInfo File, string Path)>();
        var directories = new List<string> { destination };
        var entries = 0;
        long totalBytes = 0;
        void Validate(DiscFileSystemInfo entry)
        {
            ct.ThrowIfCancellationRequested(); ValidateName(entry.Name);
            if (++entries > 2_000_000 || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Unsupported package filesystem entry.");
        }
        void Scan(DiscDirectoryInfo source, string target, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 128) throw new InvalidDataException("Package nesting exceeds the supported limit.");
            // GetFileSystemInfos returns base DiscFileSystemInfo objects, not typed
            // DiscDirectoryInfo/DiscFileInfo instances. Use the typed enumeration APIs.
            foreach (var dir in source.GetDirectories())
            {
                Validate(dir); var path = Path.Combine(target, dir.Name);
                directories.Add(path); Scan(dir, path, depth + 1);
            }
            foreach (var file in source.GetFiles())
            {
                Validate(file); totalBytes = checked(totalBytes + file.Length);
                files.Add((file, Path.Combine(target, file.Name)));
            }
        }
        Scan(fs.Root, destination, 0);
        if (files.Count == 0) throw new InvalidDataException("The package filesystem contains no files.");
        foreach (var dir in directories)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(dir) && File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Extraction destination must not contain symbolic links.");
            Directory.CreateDirectory(dir);
        }
        var timer = Stopwatch.StartNew();
        long copied = 0; var completed = 0; long lastReport = -250;
        void Report(bool force = false)
        {
            if (!force && timer.ElapsedMilliseconds - lastReport < 250) return;
            lastReport = timer.ElapsedMilliseconds;
            var fraction = totalBytes == 0 ? (double)completed / files.Count : (double)copied / totalBytes;
            progress?.Report(new($"Extracting files · {completed:N0} / {files.Count:N0} · {fraction:P1} · {copied / 1048576d:F1} / {totalBytes / 1048576d:F1} MiB", fraction));
        }
        Report(true);
        var buffer = new byte[1024 * 1024];
        foreach (var (file, path) in files)
        {
            ct.ThrowIfCancellationRequested();
            using var input = file.OpenRead();
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            long written = 0;
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, read);
                written += read; copied += read; Report();
            }
            if (written != file.Length) throw new InvalidDataException("Incomplete file in package filesystem.");
            completed++; Report();
        }
        Report(true);
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
            throw new InvalidDataException("Unsafe filename in package.");
    }
    private static void Copy(Stream input, Stream output, CancellationToken ct)
    {
        byte[] buffer = new byte[1024 * 1024];
        int read;
        while ((read = input.Read(buffer)) != 0) { ct.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
    }

    // LibXboxOne reports every decrypted page through Console. Route only this
    // execution context, leaving all other launcher output untouched. Its page
    // callbacks also provide cancellation without modifying the upstream submodule.
    private sealed class LibraryOutput(TextWriter fallback) : TextWriter
    {
        private static readonly AsyncLocal<Scope?> current = new();
        private static readonly Lazy<LibraryOutput> installed = new(() =>
        { var writer = new LibraryOutput(Console.Out); Console.SetOut(writer); return writer; });
        public override Encoding Encoding => fallback.Encoding;
        public override void Write(char value) { if (current.Value is { } scope) scope.Token.ThrowIfCancellationRequested(); else fallback.Write(value); }
        public override void WriteLine(string? value)
        {
            if (current.Value is { } scope)
            {
                scope.Token.ThrowIfCancellationRequested();
                if (value?.Contains("\"progress\"", StringComparison.Ordinal) == true) scope.OnProgress?.Invoke();
            }
            else fallback.WriteLine(value);
        }
        public static Scope Enter(CancellationToken ct) { _ = installed.Value; return current.Value = new Scope(ct); }
        public sealed class Scope(CancellationToken token) : IDisposable
        {
            public CancellationToken Token { get; } = token;
            public Action? OnProgress { get; set; }
            public void Dispose() => current.Value = null;
        }
    }
}

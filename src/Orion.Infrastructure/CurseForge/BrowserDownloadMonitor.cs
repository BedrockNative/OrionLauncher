using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Orion.Infrastructure.CurseForge;

public enum BrowserDownloadState { Waiting, MissingFolder, UnreadableFolder, Verifying, Mismatch }

/// <summary>
/// Polls only the selected directory, never its children. Copies matching downloads
/// to private staging while hashing; user files are never moved, modified or deleted.
/// Polling tolerates browser renames and works on filesystems without watcher events.
/// </summary>
public sealed class BrowserDownloadMonitor
{
    private readonly TimeSpan interval;
    public BrowserDownloadMonitor(TimeSpan? interval = null)
    {
        this.interval = interval ?? TimeSpan.FromSeconds(1);
        if (this.interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
    }
    private sealed record Stamp(long Size, DateTime Written);

    public Task WaitAsync(CfFile file, Func<string> folder, string stagingFile,
        IProgress<BrowserDownloadState>? progress, CancellationToken ct) => Task.Run(async () =>
    {
        var expectedHash = CurseForgeIntegrity.Sha1(file);
        if (System.IO.File.Exists(stagingFile) || Directory.Exists(stagingFile))
            throw new IOException("Manual download staging file already exists.");
        var filename = file.FileName;
        if (string.IsNullOrWhiteSpace(filename) || filename.Contains('/') || filename.Contains('\\') || filename.Any(char.IsControl))
            throw new InvalidDataException("Invalid expected download filename.");
        // Browsers may add (1), (2), ... when a previous download already exists.
        var pattern = new Regex("^" + Regex.Escape(Path.GetFileNameWithoutExtension(filename))
            + @"(?: ?\([0-9]+\))?" + Regex.Escape(Path.GetExtension(filename)) + @"\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        Dictionary<string, Stamp> observed = new(StringComparer.Ordinal);
        Dictionary<string, Stamp> rejected = new(StringComparer.Ordinal);
        string? lastFolder = null;
        BrowserDownloadState? lastState = null;
        void Report(BrowserDownloadState state)
        { if (lastState != state) { lastState = state; progress?.Report(state); } }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var currentFolder = folder();
            if (currentFolder != lastFolder)
            { observed.Clear(); rejected.Clear(); lastFolder = currentFolder; }
            var state = BrowserDownloadState.Waiting;
            Dictionary<string, Stamp> current = new(StringComparer.Ordinal);
            try
            {
                if (!Path.IsPathFullyQualified(currentFolder) || !Directory.Exists(currentFolder)) state = BrowserDownloadState.MissingFolder;
                else
                {
                    // Bound retained metadata. Only filename candidates are opened, not unrelated downloads.
                    foreach (var path in Directory.EnumerateFiles(currentFolder).Where(p => pattern.IsMatch(Path.GetFileName(p))).Take(256))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var info = new FileInfo(path);
                            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            var stamp = new Stamp(info.Length, info.LastWriteTimeUtc);
                            current[path] = stamp;
                            // Do not inspect browser temporary files or finalized names with pending sidecars.
                            if (File.Exists(path + ".part") || File.Exists(path + ".crdownload") || File.Exists(path + ".tmp")) continue;
                            if (stamp.Size != file.FileLength) continue;
                            if (rejected.GetValueOrDefault(path) == stamp) { state = BrowserDownloadState.Mismatch; continue; }
                            if (observed.GetValueOrDefault(path) != stamp) continue; // require two stable observations
                            Report(BrowserDownloadState.Verifying);
                            if (await CopyVerifiedAsync(path, stagingFile, file.FileLength, expectedHash, ct))
                            {
                                // Changing the selected folder also invalidates work already in flight.
                                if (folder() == currentFolder) return;
                                System.IO.File.Delete(stagingFile);
                                break;
                            }
                            rejected[path] = stamp; state = BrowserDownloadState.Mismatch;
                        }
                        catch (IOException) { /* Browser may be renaming/writing the file; retry next scan. */ }
                        catch (UnauthorizedAccessException) { state = BrowserDownloadState.UnreadableFolder; }
                    }
                }
            }
            catch (DirectoryNotFoundException) { state = BrowserDownloadState.MissingFolder; }
            catch (IOException) { state = BrowserDownloadState.UnreadableFolder; }
            catch (UnauthorizedAccessException) { state = BrowserDownloadState.UnreadableFolder; }
            observed = current;
            foreach (var stale in rejected.Keys.Where(k => !current.ContainsKey(k)).ToArray()) rejected.Remove(stale);
            Report(state);
            await Task.Delay(interval, ct);
        }
    }, ct);

    private static async Task<bool> CopyVerifiedAsync(string source, string target, long expectedSize, string hash, CancellationToken ct)
    {
        // The caller owns a unique staging filename. Never overwrite any existing file.
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
        if (input.Length != expectedSize) return false;
        FileStream output;
        try { output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("Cannot create staging copy for the manual download.", ex); }
        var success = false;
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var buffer = new byte[81920]; long length = 0; int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                length += read;
                if (length > expectedSize) return false;
                digest.AppendData(buffer, 0, read);
                try { await output.WriteAsync(buffer.AsMemory(0, read), ct); }
                catch (IOException ex) { throw new InvalidOperationException("Cannot write the manual download staging copy. Check available disk space.", ex); }
            }
            ct.ThrowIfCancellationRequested();
            success = length == expectedSize && Convert.ToHexString(digest.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase);
            return success;
        }
        finally
        {
            await output.DisposeAsync();
            if (!success) System.IO.File.Delete(target);
        }
    }
}

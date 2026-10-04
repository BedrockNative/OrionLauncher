using System.Buffers.Binary;
using System.Text.Json;
using Orion.Infrastructure.Content;

namespace Orion.Infrastructure.Rtx;

public sealed record DlssInstallation(string RelativePath, string OriginalHash, string InstalledHash, string Source);
public sealed record DlssState(string? Source, bool CanRestore, string? Error);

public sealed partial class RtxService
{
    private static string DlssPath(string root) => ContentFiles.Safe(root,
        Path.GetRelativePath(root, Path.Combine(Directory.GetParent(Directory.GetParent(Materials(root))!.FullName)!.Parent!.FullName, "nvngx_dlss.dll")));

    public static void ValidateDlss(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 256 or > 128 * 1024 * 1024 || bytes[0] != 'M' || bytes[1] != 'Z') throw new InvalidDataException("Not a supported DLSS DLL.");
        var pe = BinaryPrimitives.ReadInt32LittleEndian(bytes[60..]);
        if (pe < 64 || pe > bytes.Length - 26 || !bytes.Slice(pe, 4).SequenceEqual("PE\0\0"u8)
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes[(pe + 4)..]) != 0x8664
            || (BinaryPrimitives.ReadUInt16LittleEndian(bytes[(pe + 22)..]) & 0x2000) == 0
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes[(pe + 24)..]) != 0x20b)
            throw new InvalidDataException("DLSS must be a Windows x64 DLL, not an executable or Git LFS pointer.");
    }
    private static byte[] DlssBytes(string path)
    {
        ContentFiles.Safe(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileName(path));
        using var stream = File.OpenRead(path);
        if (stream.Length > 128 * 1024 * 1024) throw new InvalidDataException("Oversized DLSS DLL.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); ValidateDlss(bytes); return bytes;
    }
    public Task<DlssState> InspectDlssAsync(Guid id, CancellationToken ct = default) => Task.Run(() =>
    {
        using var lease = activity.Acquire(id); var root = Root(id); RecoverUnderLease(id);
        var receipt = Read<DlssInstallation>(root, "rtx/dlss/current.json");
        if (receipt is null) return new DlssState(null, false, null);
        var target = DlssPath(root); var backup = ContentFiles.Safe(root, "rtx/dlss/original.dll");
        if (receipt.RelativePath != Path.GetRelativePath(root, target) || RtxTransaction.HashFile(backup) != receipt.OriginalHash)
            return new DlssState(receipt.Source, false, "The original DLSS backup is missing or changed.");
        return new DlssState(receipt.Source, true, RtxTransaction.HashFile(target) == receipt.InstalledHash ? null : "DLSS has changed or is damaged. Restore its original backup before replacing it.");
    }, ct);

    public Task InstallDlssAsync(Guid id, string file, string source, CancellationToken ct = default) => Task.Run(() =>
    {
        using var lease = activity.Acquire(id); var root = Root(id); RecoverUnderLease(id);
        var target = DlssPath(root);
        if (!File.Exists(target)) throw new InvalidOperationException("This game does not contain nvngx_dlss.dll. Orion will not add unsupported DLSS integration.");
        var bytes = DlssBytes(file); var current = DlssBytes(target);
        RequireFamily(id, Orion.Domain.RtxFamily.VanillaRtx);
        var previous = Read<DlssInstallation>(root, "rtx/dlss/current.json");
        var hash = RtxTransaction.Hash(current);
        if (previous is not null && (previous.RelativePath != Path.GetRelativePath(root, target) || hash != previous.InstalledHash
            || RtxTransaction.HashFile(ContentFiles.Safe(root, "rtx/dlss/original.dll")) != previous.OriginalHash))
            throw new IOException("DLSS files changed outside Orion; restore the original backup first.");
        Dictionary<string, byte[]> writes = new() { [Path.GetRelativePath(root, target)] = bytes };
        if (previous is null) writes.Add("rtx/dlss/original.dll", current);
        writes.Add("rtx/dlss/current.json", JsonSerializer.SerializeToUtf8Bytes(new DlssInstallation(Path.GetRelativePath(root, target), previous?.OriginalHash ?? hash, RtxTransaction.Hash(bytes), source)));
        RtxTransaction.Apply(root, writes, ct);
    }, ct);

    public Task RestoreDlssAsync(Guid id, CancellationToken ct = default) => Task.Run(() =>
    {
        using var lease = activity.Acquire(id); var root = Root(id); RecoverUnderLease(id);
        var receipt = Read<DlssInstallation>(root, "rtx/dlss/current.json"); if (receipt is null) return;
        var target = DlssPath(root); var backup = ContentFiles.Safe(root, "rtx/dlss/original.dll");
        if (receipt.RelativePath != Path.GetRelativePath(root, target) || RtxTransaction.HashFile(backup) != receipt.OriginalHash)
            throw new IOException("The original DLSS backup is damaged; it cannot be restored safely.");
        Dictionary<string, byte[]> writes = new() { [receipt.RelativePath] = DlssBytes(backup), ["rtx/dlss/current.json"] = "null"u8.ToArray() };
        if (File.Exists(target) && RtxTransaction.HashFile(target) != receipt.InstalledHash)
            writes.Add("rtx/dlss/quarantine-" + Guid.NewGuid().ToString("N") + ".dll", File.ReadAllBytes(target));
        RtxTransaction.Apply(root, writes, ct);
    }, ct);
}

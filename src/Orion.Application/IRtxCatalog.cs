using Orion.Domain;

namespace Orion.Application;

public interface IRtxCatalog
{
    bool UsedCache { get; }
    Task<IReadOnlyList<RtxPreset>> PresetsAsync(CancellationToken ct);
    Task<IReadOnlyList<RtxTexturePack>> TexturesAsync(CancellationToken ct);
    Task DownloadPresetAsync(RtxPreset preset, string directory, IProgress<OperationProgress>? progress, CancellationToken ct);
    Task DownloadTextureAsync(RtxTexturePack pack, string path, IProgress<OperationProgress>? progress, CancellationToken ct);
    Task<RtxPreset> ResolveLinkAsync(RtxLink link, CancellationToken ct) => throw new NotSupportedException("BetterRTX links are unavailable.");
    Task<string> DownloadDlssAsync(string path, CancellationToken ct) => throw new NotSupportedException("DLSS downloads are unavailable.");
    Task<IReadOnlyList<RtxCreatorVersion>> CreatorVersionsAsync(CancellationToken ct) => throw new NotSupportedException("Creator unavailable.");
    Task<RtxCreatorForm> CreatorFormAsync(string version, CancellationToken ct) => throw new NotSupportedException("Creator unavailable.");
    Task<RtxPreset> BuildCreatorAsync(string version, RtxCreatorForm form, IReadOnlyDictionary<string, System.Text.Json.JsonElement> values,
        IProgress<OperationProgress>? progress, CancellationToken ct) => throw new NotSupportedException("Creator unavailable.");
}

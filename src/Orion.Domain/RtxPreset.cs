namespace Orion.Domain;

public sealed record RtxPreset(string Id, string Name, string? GameVersion, string? Release,
    string? ImageUrl, IReadOnlyDictionary<string, Uri> Materials, bool VersionFromAssetPath = false)
{
    public Uri Page => new($"https://bedrock.graphics/pack/{Uri.EscapeDataString(Id)}");
}

public sealed record RtxTexturePack(string Name, string Version, ReleaseAsset Asset, string? PackId = null);
public enum RtxCompatibility { Matching, NewerGame, OlderGame, Unknown }
public enum RtxFamily { BetterRtx, VanillaRtx }
public sealed record RtxConfiguration(bool EnableOnLaunch = false, bool DisableVSync = false, bool AdvancedVideo = false);
public sealed record RtxInstallation(string Name, string? TargetVersion, string GameVersion,
    string Folder, IReadOnlyDictionary<string, string> OriginalPaths, IReadOnlyDictionary<string, string> Hashes,
    string? PresetId = null, bool Recovered = false, string? AcceptedNewerGameVersion = null);
public sealed record RtxInstanceState(RtxConfiguration Configuration, RtxInstallation? Installation,
    string? VerificationError = null, bool CanRestore = false, string? FamilyConflict = null, string? CompatibilityError = null);

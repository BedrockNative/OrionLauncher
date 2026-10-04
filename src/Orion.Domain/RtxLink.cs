namespace Orion.Domain;

/// <summary>Untrusted browser input is data, never a command or arbitrary download URL.</summary>
public sealed record RtxLink(string Kind, string Id)
{
    public static RtxLink Parse(string value)
    {
        if (value.Length > 256 || value.Contains("..", StringComparison.Ordinal) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "brtx"
            || uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Use brtx://preset/ID or brtx://creator/HASH.");
        var kind = uri.Host; var id = uri.AbsolutePath.TrimStart('/');
        if (kind is not ("preset" or "creator") || id.Length is < 1 or > 128
            || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid BetterRTX link identifier.");
        return new(kind, id);
    }
    public override string ToString() => $"brtx://{Kind}/{Id}";
    public RtxPreset CreatorPreset()
    {
        if (Kind != "creator") throw new InvalidOperationException("Not a creator link.");
        return new("creator-" + Id, "BetterRTX Creator · " + Id[..Math.Min(12, Id.Length)], null, "Creator", null,
            new Dictionary<string, Uri> {
                ["RTXStub"] = new($"https://bedrock.graphics/build/{Id}/stubs/RTXStub.material.bin"),
                ["RTXPostFX.Bloom"] = new($"https://bedrock.graphics/build/{Id}/RTXPostFX.Bloom.material.bin"),
                ["RTXPostFX.Tonemapping"] = new($"https://bedrock.graphics/build/{Id}/RTXPostFX.Tonemapping.material.bin") });
    }
}

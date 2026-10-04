namespace Orion.Domain;

public sealed record GameVersion(string Version, string Channel, Uri Download)
{
    public override string ToString() => $"{Version} · {Channel}";
}

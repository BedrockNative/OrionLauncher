namespace Orion.Domain;

public sealed record GameInstance(Guid Id, string Name, string Version, string Channel,
    DateTimeOffset CreatedAt, DateTimeOffset? LastPlayedAt = null)
{
    public InstanceLaunchOptions LaunchOptions { get; init; } = new();
    public string? CoverId { get; init; }
    public bool TrackUpdates { get; init; }
    /// <summary>A newer same-channel version installed on the next launch.</summary>
    public string? TargetVersion { get; init; }
    public bool DesktopShortcut { get; init; }

    /// <summary>Selects Latest tracking or a fixed version. Never downgrades or changes channel.</summary>
    public GameInstance WithVersionSelection(string version, bool trackUpdates)
    {
        if (trackUpdates) return this with { TrackUpdates = true, TargetVersion = null };
        if (!System.Version.TryParse(version, out var target) || !System.Version.TryParse(Version, out var installed))
            throw new ArgumentException("Invalid Minecraft version.", nameof(version));
        if (target < installed) throw new ArgumentException("Instances can only be updated to a newer version.", nameof(version));
        return this with { TrackUpdates = false, TargetVersion = target == installed ? null : version };
    }

    public static void ValidateCoverId(string? id)
    {
        if (id is not null && (id.Length is < 1 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new ArgumentException("Invalid instance cover.");
    }

    public static GameInstance Create(string name, string version, string channel) =>
        new(Guid.NewGuid(), ValidateName(name), version, channel, DateTimeOffset.UtcNow);

    public static string ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
            throw new ArgumentException("Instance names must contain 1–80 printable characters.", nameof(name));
        return name;
    }
}

namespace Orion.Domain;

public sealed record GameInstance(Guid Id, string Name, string Version, string Channel,
    DateTimeOffset CreatedAt, DateTimeOffset? LastPlayedAt = null)
{
    public InstanceLaunchOptions LaunchOptions { get; init; } = new();
    public string? CoverId { get; init; }
    public bool DesktopShortcut { get; init; }

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

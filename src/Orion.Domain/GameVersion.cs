namespace Orion.Domain;

public sealed record GameVersion(string Version, string Channel, Uri Download)
{
    public bool TrackUpdates { get; init; }
    public string DisplayName => TrackUpdates ? (Channel == "Release" ? "Latest Release" : "Latest Preview") : $"{Version} · {Channel}";
    public static IReadOnlyList<GameVersion> WithRollingChoices(IReadOnlyList<GameVersion> versions)
    {
        var choices = new List<GameVersion>();
        foreach (var channel in new[] { "Release", "Preview" })
        {
            var latest = versions.Where(v => v.Channel == channel)
                .OrderByDescending(v => System.Version.Parse(v.Version)).FirstOrDefault();
            if (latest is not null) choices.Add(latest with { TrackUpdates = true });
        }
        choices.AddRange(versions);
        return choices;
    }
    /// <summary>Same-channel catalog versions newer than the installed game, newest first.</summary>
    public static IReadOnlyList<GameVersion> UpgradesFor(GameInstance instance, IReadOnlyList<GameVersion> versions)
    {
        if (!System.Version.TryParse(instance.Version, out var installed)) return [];
        return versions.Where(v => v.Channel == instance.Channel && System.Version.Parse(v.Version) > installed)
            .OrderByDescending(v => System.Version.Parse(v.Version)).ToArray();
    }
    public override string ToString() => DisplayName;
}

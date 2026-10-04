namespace Orion.Domain;

public enum ContentKind { World, Addon, Texture }
// Paths are instance-relative opaque identifiers, never paths supplied directly by the UI.
public sealed record ContentProfile(string Id, string Label)
{
    public override string ToString() => Label;
}
public sealed record ContentEntry(string Id, string Name, ContentKind Kind, string Version,
    string? PackId, string? ProfileId, bool Archived = false, string? Warning = null)
{
    public string Description { get; init; } = "";
    public string MinimumEngineVersion { get; init; } = "";
    public string? IconPath { get; init; }
    public string? FallbackIconPath { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public bool Shared { get; init; }
}
public sealed record LibraryContent(Guid Id, string Name, IReadOnlyList<ContentEntry> Entries);
public sealed record ContentTarget(Guid InstanceId, string? ProfileId);
public sealed record ContentSnapshot(IReadOnlyList<ContentProfile> Profiles, IReadOnlyList<ContentEntry> Entries);

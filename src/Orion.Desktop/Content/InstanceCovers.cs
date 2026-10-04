namespace Orion.Desktop.Content;

public sealed record InstanceCover(string Id, string LabelKey, Uri Image, Uri Source);

/// <summary>Small, fixed selection; instance metadata cannot introduce arbitrary URLs.</summary>
public static class InstanceCovers
{
    public static IReadOnlyList<InstanceCover> All { get; } =
    [
        new("mountains", "CoverMountains",
            new("https://upload.wikimedia.org/wikipedia/commons/9/9a/Minecraft_-_1.18_mountains.jpg"),
            new("https://commons.wikimedia.org/wiki/File:Minecraft_-_1.18_mountains.jpg")),
        new("jungle", "CoverJungle",
            new("https://upload.wikimedia.org/wikipedia/commons/3/37/Minecraft_-_Jungle.jpg"),
            new("https://commons.wikimedia.org/wiki/File:Minecraft_-_Jungle.jpg")),
        new("lush-caves", "CoverCaves",
            new("https://upload.wikimedia.org/wikipedia/commons/9/97/Minecraft_-_Lush_caves.jpg"),
            new("https://commons.wikimedia.org/wiki/File:Minecraft_-_Lush_caves.jpg"))
    ];
    public static InstanceCover? Find(string? id) => All.FirstOrDefault(c => c.Id == id);
}

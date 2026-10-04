namespace Orion.Infrastructure.CurseForge;

public sealed record CfLinks(string WebsiteUrl);
public sealed record CfAuthor(string Name);
public sealed record CfAsset(string? ThumbnailUrl, string? Url);
public sealed record CfCategory(int Id, string Name, bool IsClass)
{
    public override string ToString() => Name;
}
public sealed record CfProject(int Id, int GameId, string Name, string Summary, CfAuthor[] Authors,
    CfLinks Links, bool? AllowModDistribution, double DownloadCount)
{
    public string AuthorNames => string.Join(", ", Authors.Select(a => a.Name));
    public CfAsset? Logo { get; init; }
    public string? CoverUrl => new[] { Logo?.ThumbnailUrl, Logo?.Url }.FirstOrDefault(url =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && CurseForgeClient.IsDownloadUri(uri));
}
public sealed record CfHash(string Value, int Algo);
public sealed record CfDependency(int ModId, int RelationType);
public sealed record CfFile(int Id, int ModId, int GameId, string DisplayName, string FileName, long FileLength,
    bool IsAvailable, string? DownloadUrl, CfHash[] Hashes, string[] GameVersions, CfDependency[] Dependencies)
{
    public string VersionLabel => $"{DisplayName} · {string.Join(", ", GameVersions)}";
    public override string ToString() => VersionLabel;
}
public sealed record CfPagination(int Index, int ResultCount, int TotalCount);
public sealed record CfPage<T>(T[] Data, CfPagination Pagination);

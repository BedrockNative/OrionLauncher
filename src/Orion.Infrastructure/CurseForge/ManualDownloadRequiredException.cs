namespace Orion.Infrastructure.CurseForge;

/// <summary>A browser handoff, not permission to synthesize a CDN URL.</summary>
public sealed class ManualDownloadRequiredException(CfFile file, Uri page)
    : InvalidOperationException("This file must be downloaded through the CurseForge website.")
{
    public CfFile File { get; } = file;
    public Uri Page { get; } = page;
}

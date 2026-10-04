using Orion.Application;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Linux;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Runtime;
using Orion.Infrastructure.Storage;

namespace Orion.Desktop.Composition;

/// <summary>The application's single composition root; dependencies are explicit and owned here.</summary>
public sealed class LauncherServices : IAsyncDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient downloads = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient rtxHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    // Avatars are public images. Never follow redirects to arbitrary hosts or attach credentials.
    private readonly HttpClient pictures = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    public AppPaths Paths { get; }
    public LauncherSettings Settings { get; set; }
    public SettingsStore SettingsStore { get; }
    public InstanceService Instances { get; }
    public VersionCatalog Catalog { get; }
    public RuntimeManager Runtimes { get; }
    public AccountService Account { get; }
    public GameLauncher Games { get; }
    public Orion.Infrastructure.Content.InstanceContentService Content { get; }
    public Orion.Infrastructure.Content.ContentLibraryService ContentLibrary { get; }
    public Orion.Infrastructure.CurseForge.CurseForgeClient CurseForge { get; } = new();
    public Orion.Infrastructure.Rtx.RtxCatalog RtxCatalog { get; }
    public Orion.Infrastructure.Rtx.RtxService Rtx { get; }
    public RtxProtocolIntegration RtxLinks { get; }
    public InstallationQueue Downloads { get; }
    public Orion.Desktop.Content.CoverStore Covers { get; }
    public Orion.Desktop.Content.CoverStore ProjectCovers { get; }
    public Orion.Desktop.Content.AvatarStore Avatars { get; }
    private XodusService Xodus { get; }
    private Task? disposal;

    public LauncherServices(AppPaths paths, LauncherSettings settings, IReadOnlyList<string> command,
        IReadOnlyList<string>? supervisorCommand = null)
    {
        Paths = paths; Settings = settings; SettingsStore = new(paths);
        Covers = new(http, Path.Combine(paths.Cache, "covers"));
        ProjectCovers = new(pictures, Path.Combine(paths.Cache, "curseforge-covers"));
        Avatars = new(pictures, Path.Combine(paths.Cache, "xbox-avatars"));
        var releases = new GitHubReleaseClient(http, Path.Combine(paths.Cache, "releases"));
        Runtimes = new(paths, releases, new(downloads), Path.Combine(AppContext.BaseDirectory, "runtimes"));
        var runner = new ProcessRunner(supervisorCommand ?? command);
        var environment = new XodusEnvironment(paths);
        Xodus = new(paths, environment, runner);
        Account = new(paths, Runtimes, runner, environment, Xodus);
        Catalog = new(http, paths);
        var activity = new InstanceActivity();
        Content = new(paths, activity);
        ContentLibrary = new(paths, activity, Content);
        RtxCatalog = new(rtxHttp, releases, Path.Combine(paths.Cache, "rtx"), downloads);
        Rtx = new(paths, activity, RtxCatalog, Content);
        RtxLinks = new(paths, command, Path.Combine(AppContext.BaseDirectory, "Assets", "orion.svg"));
        Games = new(paths, Runtimes, runner, Account, activity, Content, Rtx);
        var desktop = new DesktopIntegration(paths, command,
            Path.Combine(AppContext.BaseDirectory, "Assets", "orion.svg"));
        Instances = new(new InstanceRepository(paths), new GameInstaller(paths, Runtimes, runner, environment, Xodus, new(downloads)), Games, desktop, activity);
        Downloads = new(paths, async (instance, source, progress, ct) => { await Instances.CreateAsync(instance, source, progress, ct); });
    }

    public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        try { await Downloads.DisposeAsync(); }
        finally
        {
            try { await Xodus.DisposeAsync(); }
            finally { CurseForge.Dispose(); Covers.Dispose(); ProjectCovers.Dispose(); http.Dispose(); downloads.Dispose(); pictures.Dispose(); rtxHttp.Dispose(); }
        }
    }
}

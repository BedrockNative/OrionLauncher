using System.Security.Cryptography;
using System.Text;

namespace Orion.Infrastructure.Storage;

public sealed class AppPaths
{
    public string Data { get; }
    public string Config { get; }
    public string Cache { get; }
    public string Runtime { get; }
    public string Applications { get; }
    public string Instances => Path.Combine(Data, "instances");
    public string Archives => Path.Combine(Data, "archives");
    public string Tools => Path.Combine(Data, "runtimes");
    public string Logs => Path.Combine(Data, "logs");
    public string Downloads => Path.Combine(Data, "downloads");
    public string Download(Guid id) => Path.Combine(Downloads, id.ToString("N"));
    public string InstallStage(Guid id) => Path.Combine(Instances, $".install-{id:N}");
    public string XodusProfile => Path.Combine(Config, "xodus");
    public string Identity => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Config)))[..16];
    public string SocketName => $"orion.xodus-{Identity}.sock";
    public string SocketPath => Path.Combine(Runtime, SocketName);
    public string Instance(Guid id) => Path.Combine(Instances, id.ToString("N"));
    public string Game(Guid id) => Path.Combine(Instance(id), "game");
    public string Prefix(Guid id) => Path.Combine(Instance(id), "prefix");
    public string InstanceLog(Guid id) => Path.Combine(Logs, $"instance-{id:N}.log");

    public AppPaths(string data, string config, string cache, string runtime, string applications)
    {
        Data = Path.GetFullPath(data); Config = Path.GetFullPath(config);
        Cache = Path.GetFullPath(cache); Runtime = Path.GetFullPath(runtime);
        Applications = Path.GetFullPath(applications);
    }

    public static AppPaths Discover()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        static string Xdg(string key, string fallback) =>
            Environment.GetEnvironmentVariable(key) is { } value && Path.IsPathFullyQualified(value) ? value : fallback;
        var data = Xdg("XDG_DATA_HOME", Path.Combine(home, ".local", "share"));
        var config = Xdg("XDG_CONFIG_HOME", Path.Combine(home, ".config"));
        var cache = Xdg("XDG_CACHE_HOME", Path.Combine(home, ".cache"));
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime) || !Path.IsPathFullyQualified(runtime))
            throw new InvalidOperationException("A Linux desktop session with XDG_RUNTIME_DIR is required.");
        return new(Path.Combine(data, "orion-launcher"), Path.Combine(config, "orion-launcher"),
            Path.Combine(cache, "orion-launcher"), runtime, Path.Combine(data, "applications"));
    }

    public void EnsureDirectories()
    {
        foreach (var path in new[] { Data, Config, Cache, Instances, Archives, Tools, Logs, XodusProfile })
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

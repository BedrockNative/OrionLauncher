using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Runtime;

public sealed class XodusEnvironment(AppPaths paths, Guid? session = null, string? accountId = null)
{
    public string SocketName => session is { } id ? $"orion.xodus-{paths.Identity}-{id:N}.sock" : paths.SocketName;
    public string SocketPath => Path.Combine(paths.Runtime, SocketName);
    public Dictionary<string, string?> Create(string? prefix = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["XODUS_CONFIG_DIR"] = paths.XodusProfile,
            ["XODUS_SOCK_NAME"] = SocketName,
            ["XODUS_SOCKET"] = SocketPath,
            ["XODUS_ACCOUNT_ID"] = accountId,
            ["XDG_RUNTIME_DIR"] = paths.Runtime,
            ["XODUS_LOG"] = "warn",
            ["WINEPREFIX"] = prefix,
            ["WINEDEBUG"] = "-all",
            ["WINEBOOT_HIDE_DIALOG"] = "1",
            ["WINEDLLOVERRIDES"] = null,
            ["WINEDLLPATH"] = null,
            ["WINELOADER"] = null,
            ["WINESERVER"] = null,
            ["WINEARCH"] = null,
            ["WINE_DLL_FILE_MAP"] = null
        };
        // Runtime updates live outside the portable bundle, so they cannot use
        // its relative RPATHs. Supply native libraries only to owned processes.
        var native = Path.Combine(AppContext.BaseDirectory, "native");
        if (Directory.Exists(native))
        {
            var inherited = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            values["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(inherited) ? native : native + ":" + inherited;
            var webkit = Environment.GetEnvironmentVariable("ORION_WEBKIT_LIBRARY_DIR");
            if (!string.IsNullOrEmpty(webkit) && File.Exists(Path.Combine(webkit, "libwebkit2gtk-4.1.so.0")))
                values["LD_LIBRARY_PATH"] = webkit + ":" + values["LD_LIBRARY_PATH"];
        }
        return values;
    }
}

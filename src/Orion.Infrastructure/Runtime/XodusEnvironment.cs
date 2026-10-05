using System.Security.Cryptography;
using System.Text;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Runtime;

public sealed class XodusEnvironment(AppPaths paths, Guid? session = null, string? accountId = null)
{
    public string SocketName { get; private init; } = session is { } id ? $"orion.xodus-{paths.Identity}-{id:N}.sock" : paths.SocketName;
    public static XodusEnvironment ForAccount(AppPaths paths, string? accountId)
    {
        // Include the profile in a short digest to stay within Unix socket path limits.
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(paths.Config + "\0" + accountId)));
        return new(paths, accountId: accountId) { SocketName = $"orion.xodus-{hash[..24]}.sock" };
    }
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

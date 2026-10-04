using Orion.Domain;

namespace Orion.Infrastructure.Games;

/// <summary>The purchase gate and retained-license contract belongs to Xodus, not Orion.</summary>
public static class XodusGameCommands
{
    public const string MinecraftWindowsProduct = "9NBLGGH2JHXJ";

    public static void RequirePurchaseApi(RuntimeInstallation runtime)
    {
        var version = runtime.Tag.TrimStart('v').Split('-')[0];
        if (!Version.TryParse(version, out var parsed) || parsed < new Version(0, 2, 0))
            throw new InvalidOperationException("Xodus 0.2.0 or newer is required for purchase verification. Update the runtime before installing or playing.");
    }

    public static string[] Install(string source, string destination, bool local) =>
        ["install-owned", MinecraftWindowsProduct, local ? "file://" + source : source, destination];

    public static void RequireLaunchArgumentsApi(RuntimeInstallation runtime, InstanceLaunchOptions options)
    {
        if (options.Arguments.Length == 0) return;
        if (!Version.TryParse(runtime.Tag.TrimStart('v').Split('-')[0], out var version) || version < new Version(0, 3, 0))
            throw new InvalidOperationException("Game arguments require Xodus 0.3.0 or newer. Update the runtime first.");
    }

    public static string[] Play(string game, string wine, string executable, IReadOnlyList<string>? arguments = null)
    {
        string[] command = ["run", game, wine, "--exe", executable, "--offline-license"];
        return arguments is { Count: > 0 } ? [.. command, "--", .. arguments] : command;
    }
}

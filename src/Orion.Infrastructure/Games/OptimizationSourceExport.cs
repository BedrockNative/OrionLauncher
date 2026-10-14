namespace Orion.Infrastructure.Games;

/// <summary>One-time bridge for legacy Xodus installs. Runs inside Orion, without a shell script.</summary>
public static class OptimizationSourceExport
{
    public const string DestinationVariable = "ORION_EXPORT_GAME_IMAGE";
    public static int Run(string[] args, string destination)
    {
        try
        {
            if (args.Length != 1 || !Path.IsPathFullyQualified(destination)) throw new InvalidDataException("Invalid executable export request.");
            var entries = (Environment.GetEnvironmentVariable("WINE_DLL_FILE_MAP") ?? "").Split('|');
            if (entries.Length != 1) throw new InvalidDataException("This legacy installation needs full reinstallation before enabling optimizations.");
            var pair = entries[0].Split(':', 2);
            if (pair.Length != 2 || !string.Equals(pair[1], args[0], StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(pair[0], out var fd) || fd < 3) throw new InvalidDataException("Missing decrypted executable descriptor.");
            var source = $"/proc/self/fd/{fd}";
            if (!(new FileInfo(source).LinkTarget?.StartsWith("/memfd:", StringComparison.Ordinal) ?? false))
                throw new InvalidDataException("Expected an in-memory decrypted executable.");
            using var input = File.OpenRead(source);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            input.CopyTo(output);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
}

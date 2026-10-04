namespace Orion.Infrastructure.Games;

public static class GameLayout
{
    public static string Executable(string gameDirectory)
    {
        var matches = Directory.EnumerateFiles(gameDirectory, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p).Equals("Minecraft.Windows.exe", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(p).Equals("Minecraft.Windows.Preview.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException("The package must contain one Minecraft Bedrock GDK executable.");
    }

    public static void Validate(string directory)
    {
        var executable = Executable(directory);
        if (new FileInfo(executable).Length == 0 || !File.Exists(Path.Combine(directory, ".xodus-streaming.msixvc")))
            throw new InvalidDataException("Xodus did not finish extracting the package. See the installation log.");
    }
}

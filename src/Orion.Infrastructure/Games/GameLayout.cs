using System.Reflection.PortableExecutable;

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

    public static bool IsFullyDecrypted(string directory) => File.Exists(Path.Combine(directory, XvdPackageExtractor.Marker));

    public static void Validate(string directory) => ValidatedExecutable(directory);

    public static string ValidatedExecutable(string directory)
    {
        var executable = Executable(directory);
        if (new FileInfo(executable).Length == 0 || (!IsFullyDecrypted(directory) && !File.Exists(Path.Combine(directory, ".xodus-streaming.msixvc"))))
            throw new InvalidDataException("Xodus did not finish extracting the package. See the installation log.");
        if (IsFullyDecrypted(directory))
        {
            using var stream = File.OpenRead(executable);
            using var pe = new PEReader(stream);
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus
                || pe.PEHeaders.SectionHeaders.Length == 0
                || pe.PEHeaders.SectionHeaders.Any(s => s.PointerToRawData < 0 || s.SizeOfRawData < 0
                    || (long)s.PointerToRawData + s.SizeOfRawData > stream.Length))
                throw new InvalidDataException("The decrypted package does not contain a complete x64 executable.");
        }
        return executable;
    }
}

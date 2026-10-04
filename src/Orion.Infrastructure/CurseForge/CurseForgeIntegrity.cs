namespace Orion.Infrastructure.CurseForge;

public static class CurseForgeIntegrity
{
    public static string Sha1(CfFile file)
    {
        if (file.FileLength is <= 0 or > 8L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Unsupported download size.");
        return file.Hashes?.FirstOrDefault(h => h.Algo == 1 && h.Value is { Length: 40 } && h.Value.All(Uri.IsHexDigit))?.Value
            ?? throw new InvalidDataException("The file has no SHA-1 integrity hash. Automatic import cannot verify it.");
    }
}

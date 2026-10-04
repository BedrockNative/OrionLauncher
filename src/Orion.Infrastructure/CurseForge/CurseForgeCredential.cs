using System.Security.Cryptography;
using System.Text;

namespace Orion.Infrastructure.CurseForge;

/// <summary>Build-time obfuscation, NOT encryption or a confidentiality boundary on the user's machine.</summary>
public static class CurseForgeCredential
{
    public const string EnvironmentKey = "CURSEFORGE_API_KEY";
    public const string EnvironmentFile = "ORION_CURSEFORGE_API_KEY_FILE";
    private static string? capturedOverride;
    public static void CaptureDeveloperOverride()
    {
        try { capturedOverride = ReadOverride(Environment.GetEnvironmentVariable); }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentKey, null);
            Environment.SetEnvironmentVariable(EnvironmentFile, null);
        }
    }
    public static string Read()
        => capturedOverride ?? Read(Environment.GetEnvironmentVariable);

    // Injectable environment avoids mutating process-wide state in tests.
    public static string Read(Func<string, string?> environment) => ReadOverride(environment) ?? ReadEmbedded();
    private static string? ReadOverride(Func<string, string?> environment)
    {
        var value = environment(EnvironmentKey);
        if (!string.IsNullOrEmpty(value)) return Validate(value);
        var path = environment(EnvironmentFile);
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            if (!Path.IsPathFullyQualified(path) || new FileInfo(path) is not { LinkTarget: null, Length: > 0 and <= 4096 }) throw new IOException();
            if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new IOException();
            using var reader = File.OpenText(path);
            var buffer = new char[4097];
            var length = reader.ReadBlock(buffer, 0, buffer.Length);
            if (length > 4096) throw new IOException();
            return Validate(new string(buffer, 0, length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new InvalidOperationException("Cannot read the CurseForge developer key file. Use an absolute path to a private file (chmod 600), up to 4096 bytes."); }
    }
    private static string Validate(string value)
    {
        value = value.Trim();
        if (value.Length is < 1 or > 512 || value.Any(char.IsControl))
            throw new InvalidOperationException("The CurseForge developer key is invalid. Expected 1–512 characters without control characters.");
        return value;
    }
    private static string ReadEmbedded()
    {
        using var stream = typeof(CurseForgeCredential).Assembly.GetManifestResourceStream("Orion.CurseForge.Credential");
        if (stream is null || stream.Length < 8 || stream.Length > 2048) return "";
        using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 0x43464f31) return "";
        var length = reader.ReadInt32();
        if (length is < 1 or > 512 || stream.Length != 8 + length * 2) return "";
        var clear = new byte[length];
        try
        {
            for (var i = 0; i < length; i++) clear[i] = (byte)(reader.ReadByte() ^ reader.ReadByte());
            return Encoding.UTF8.GetString(clear);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}

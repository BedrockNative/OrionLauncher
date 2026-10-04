using System.Security.Cryptography;
using System.Text;

// This executable is built WITHOUT the secret. Only its execution receives the CI environment secret.
// Never log the key, pass it through MSBuild properties, or generate C# containing it.
if (args.Length != 1) { Console.Error.WriteLine("Expected an output path."); return 1; }
var value = Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
{ Console.Error.WriteLine("Missing or invalid CurseForge build credential."); return 1; }
var key = Encoding.UTF8.GetBytes(value);
var mask = RandomNumberGenerator.GetBytes(key.Length);
try
{
    var path = Path.GetFullPath(args[0]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    using var writer = new BinaryWriter(stream);
    writer.Write(0x43464f31); writer.Write(key.Length);
    for (var i = 0; i < key.Length; i++) { writer.Write(mask[i]); writer.Write((byte)(key[i] ^ mask[i])); }
    return 0;
}
finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(mask); }

using System.Text;
using System.Text.RegularExpressions;

namespace Orion.Infrastructure.Processes;

/// <summary>Reads a bounded snapshot without loading the whole journal into memory.</summary>
public static partial class LogTail
{
    public const int MaximumBytes = 128 * 1024;
    public static async Task<string> ReadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            var start = Math.Max(0, stream.Length - MaximumBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var bytes = new byte[MaximumBytes];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), ct);
                if (read == 0) break;
                length += read;
            }
            var text = Encoding.UTF8.GetString(bytes, 0, length);
            if (start > 0)
            {
                var newline = text.IndexOf('\n');
                if (newline >= 0 && newline < text.Length - 1) text = text[(newline + 1)..];
                text = "…\n" + text;
            }
            return EscapeSequences().Replace(text, "").Replace("\r\n", "\n").Replace('\r', '\n');
        }
        catch (FileNotFoundException) { return ""; }
        catch (DirectoryNotFoundException) { return ""; }
    }
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\))|[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]")]
    private static partial Regex EscapeSequences();
}

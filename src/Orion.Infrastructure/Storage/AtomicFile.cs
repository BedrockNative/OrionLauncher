using System.Text.Json;

namespace Orion.Infrastructure.Storage;

public static class AtomicFile
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task WriteAsync(string path, string content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static Task WriteJsonAsync<T>(string path, T value, CancellationToken ct = default) =>
        WriteAsync(path, JsonSerializer.Serialize(value, Json), ct);

    public static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct);
    }
}

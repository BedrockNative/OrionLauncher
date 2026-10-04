using Orion.Application;
using Orion.Domain;
using Orion.Infrastructure.Storage;

namespace Orion.Infrastructure.Linux;

public sealed class DesktopIntegration(AppPaths paths, IReadOnlyList<string> command,
    string iconPath) : IDesktopIntegration
{
    private const string Marker = "X-Orion-Managed=true";
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task SynchronizeAsync(IReadOnlyList<GameInstance> instances, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) return;
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(paths.Applications);
            var wanted = instances.Where(i => i.DesktopShortcut).ToDictionary(i => $"io.bedrocknative.orion.{i.Id:N}.desktop");
            foreach (var (file, instance) in wanted)
            {
                var path = Path.Combine(paths.Applications, file);
                if (new FileInfo(path).LinkTarget is not null) throw new IOException($"Refusing to overwrite a linked desktop entry: {path}");
                if (File.Exists(path) && !(await File.ReadAllLinesAsync(path, cancellationToken)).Contains(Marker))
                    throw new IOException($"Desktop entry is not managed by Orion: {path}");
                await AtomicFile.WriteAsync(path, Render(instance, command, iconPath), cancellationToken);
            }
            foreach (var path in Directory.EnumerateFiles(paths.Applications, "io.bedrocknative.orion.*.desktop"))
                if (!wanted.ContainsKey(Path.GetFileName(path)) && new FileInfo(path).LinkTarget is null &&
                    (await File.ReadAllLinesAsync(path, cancellationToken)).Contains(Marker)) File.Delete(path);
        }
        finally { gate.Release(); }
    }

    public static string Render(GameInstance instance, IReadOnlyList<string> command, string icon) =>
        "[Desktop Entry]\nType=Application\nVersion=1.0\n" +
        $"Name={Value($"Minecraft — {instance.Name}")}\nComment={Value(instance.Version + " · " + instance.Channel)}\n" +
        $"Exec={string.Join(' ', command.Concat(["--launch", instance.Id.ToString(), "--background"]).Select(Argument))}\n" +
        $"Icon={Value(icon)}\nTerminal=false\nCategories=Game;\nStartupNotify=false\n{Marker}\n";

    private static string Value(string text) => text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    public static string Argument(string text)
    {
        if (text.Any(char.IsControl)) throw new ArgumentException("Control characters are not valid in desktop arguments.");
        var escaped = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (c is '\\' or '"' or '`' or '$') escaped.Append('\\');
            escaped.Append(c == '%' ? "%%" : c.ToString());
        }
        return '"' + Value(escaped.ToString()) + '"';
    }
}

using System.Text;
using Orion.Infrastructure.Processes;

namespace Orion.Infrastructure.Linux;

/// <summary>Explicitly authorized, reversible exception in the user's Umbriel configuration.</summary>
public sealed class CompositorOpacityRule(string configPath,
    Func<string, CancellationToken, Task>? validate = null)
{
    private const string Marker = "# BEGIN ORION MANAGED OPACITY v1";
    public const string Block = "\n# BEGIN ORION MANAGED OPACITY v1\n[[window_rule]]\nmatch.app_id = \"^OrionLauncher$\"\nopacity = 1.0\n# END ORION MANAGED OPACITY v1\n";
    public string ConfigPath { get; } = Path.GetFullPath(configPath);
    public bool IsInstalled => Read().Contains(Block, StringComparison.Ordinal);

    private string Read()
    {
        for (var path = ConfigPath; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked configuration paths require manual configuration.");
        if (new FileInfo(ConfigPath).Length > 1024 * 1024) throw new IOException("Configuration is too large.");
        return new UTF8Encoding(false, true).GetString(File.ReadAllBytes(ConfigPath));
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        var before = Read();
        if (before.Contains(Marker, StringComparison.Ordinal) &&
            (!before.Contains(Block, StringComparison.Ordinal) || before.IndexOf(Marker, StringComparison.Ordinal) != before.LastIndexOf(Marker, StringComparison.Ordinal)))
            throw new IOException("The managed rule was edited. Review it manually; Orion will not overwrite it.");
        var installed = before.Contains(Block, StringComparison.Ordinal);
        if (installed == enabled) return;
        var after = enabled ? before + Block : before.Replace(Block, "", StringComparison.Ordinal);
        var temporary = Path.Combine(Path.GetDirectoryName(ConfigPath)!, $".orion-opacity-{Guid.NewGuid():N}.toml");
        try
        {
            await WritePrivateAsync(temporary, after, ct);
            if (validate is not null) await validate(temporary, ct);
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await new ProcessRunner().CaptureAsync(new("umbriel", ["config", "validate", "-c", temporary], Path.GetDirectoryName(ConfigPath)!),
                    timeout.Token, "Umbriel rejected the proposed configuration. Nothing was changed.");
            }
            ct.ThrowIfCancellationRequested();
            if (Read() != before) throw new IOException("The configuration changed during validation. Try again.");
            // Keep a private recovery copy; undo removes only our block, not the user's subsequent edits.
            var backup = ConfigPath + $".orion-backup-{Guid.NewGuid():N}";
            await WritePrivateAsync(backup, before, ct);
            File.SetUnixFileMode(temporary, File.GetUnixFileMode(ConfigPath));
            File.Move(temporary, ConfigPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task WritePrivateAsync(string path, string content, CancellationToken ct)
    {
        await using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        await using var writer = new StreamWriter(file, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), ct);
    }
}

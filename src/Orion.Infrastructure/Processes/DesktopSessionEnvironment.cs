namespace Orion.Infrastructure.Processes;

public sealed class DesktopSessionEnvironment(string? waylandDisplay)
{
    // Avalonia's GTK integration replaces this variable in the launcher process.
    // Children need the original desktop socket, not GTK's disabled backend.
    public const string GtkDisabledDisplay = "/proc/fake-display-to-prevent-wayland-initialization-by-gtk3";

    public void Restore(IDictionary<string, string?> environment)
    {
        if (!environment.TryGetValue("WAYLAND_DISPLAY", out var current) || current != GtkDisabledDisplay) return;
        if (string.IsNullOrEmpty(waylandDisplay) || waylandDisplay == GtkDisabledDisplay)
            environment.Remove("WAYLAND_DISPLAY");
        else
            environment["WAYLAND_DISPLAY"] = waylandDisplay;
    }
}

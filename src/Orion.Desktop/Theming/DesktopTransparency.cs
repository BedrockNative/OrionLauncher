using Orion.Desktop.I18n;

namespace Orion.Desktop.Theming;

/// <summary>Session hints identify the desktop, not whether its rules currently force opacity.</summary>
public static class DesktopTransparency
{
    public static (string Id, string Name) Detect(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var desktop = string.Join(':', environment("XDG_CURRENT_DESKTOP"), environment("XDG_SESSION_DESKTOP"), environment("DESKTOP_SESSION"))
            .ToLowerInvariant().Split([':', ';', '/', ' '], StringSplitOptions.RemoveEmptyEntries);
        bool Has(string value) => desktop.Contains(value, StringComparer.Ordinal);
        if (Has("umbriel")) return ("umbriel", "Umbriel");
        if (Has("hyprland")) return ("hyprland", "Hyprland");
        if (Has("niri")) return ("niri", "niri");
        if (Has("kde") || Has("plasma") || Has("plasmax11") || Has("plasmawayland")) return ("kde", "KDE Plasma / KWin");
        if (Has("cosmic")) return ("cosmic", "COSMIC / Pop!_OS");
        if (Has("zorin") || Has("zorinos")) return ("gnome", "Zorin OS / GNOME");
        if (Has("pop") || Has("pop:gnome")) return ("gnome", "Pop!_OS / GNOME");
        if (Has("gnome")) return ("gnome", "GNOME");
        if (Has("sway")) return ("sway", "Sway");
        if (Has("xfce")) return ("xfce", "Xfce");
        // Prefer the explicit desktop above: inherited sockets can belong to a nested session.
        if (!string.IsNullOrWhiteSpace(environment("NIRI_SOCKET"))) return ("niri", "niri");
        if (!string.IsNullOrWhiteSpace(environment("HYPRLAND_INSTANCE_SIGNATURE"))) return ("hyprland", "Hyprland");
        return ("other", environment("XDG_SESSION_TYPE") == "wayland" ? "Wayland" : "X11 / Linux");
    }

    public static string Guide(Localizer text) => Detect().Id switch
    {
        "umbriel" => text["DesktopRuleLast"] + "\n\n[[window_rule]]\nmatch.app_id = \"^OrionLauncher$\"\nopacity = 1.0",
        "niri" => text["DesktopRuleLast"] + "\n\nwindow-rule {\n    match app-id=\"^OrionLauncher$\"\n    opacity 1.0\n}",
        "hyprland" => text["DesktopRuleHyprland"],
        "kde" => text["DesktopRuleKde"],
        "gnome" => text["DesktopRuleGnome"],
        "cosmic" => text["DesktopRuleCosmic"],
        _ => text["DesktopRuleOther"]
    };
}

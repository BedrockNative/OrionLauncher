namespace Orion.Domain;

public sealed record GameResolution(int Width, int Height)
{
    public void Validate()
    {
        if (Width is < 320 or > 16384 || Height is < 200 or > 16384)
            throw new ArgumentException("Resolution must be between 320×200 and 16384×16384.");
    }
}

public sealed record InstanceLaunchOptions
{
    public string[] Arguments { get; init; } = [];
    public string LaunchCommand { get; init; } = "%command%";
    public string? AccountId { get; init; }
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.Ordinal);
    // Reserved for a future display implementation; ignored when launching.
    public GameResolution? Resolution { get; init; }
    public bool ResolutionLocked { get; init; }
    public bool Fullscreen { get; init; }
    public bool ShowLogOnLaunch { get; init; }
    public MangoHudOptions? MangoHud { get; init; } = new();

    public void Validate()
    {
        ValidateLaunchInputs();
        Resolution?.Validate();
        if (ResolutionLocked && Resolution is null)
            throw new ArgumentException("Locked monitor dimensions require a resolution.");
        if (Fullscreen && Resolution is null)
            throw new ArgumentException("Select a custom resolution before saving the fullscreen preference.");
    }

    public void ValidateLaunchInputs()
    {
        MangoHud?.Validate();
        LaunchCommandTemplate.Parse(LaunchCommand);
        if (AccountId is not null && (AccountId.Length != 64 || !AccountId.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("Invalid saved account identity.");
        if (Arguments is null || Arguments.Length > 256 || Arguments.Any(a => a is null || a.Length > 8192 || a.IndexOfAny(['\0', '\r', '\n']) >= 0))
            throw new ArgumentException("Use at most 256 single-line arguments without NUL characters (8192 characters each).");
        if (Environment is null || Environment.Count > 128)
            throw new ArgumentException("Use at most 128 environment variables.");
        foreach (var (name, value) in Environment)
        {
            if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_')
                || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                throw new ArgumentException("Environment variable names must use letters, digits and underscores and cannot start with a digit.");
            if (IsReserved(name)) throw new ArgumentException($"{name} is managed by Orion to keep accounts, sockets and Wine prefixes isolated.");
            if (value is null || value.Length > 32768 || value.IndexOfAny(['\0', '\r', '\n']) >= 0)
                throw new ArgumentException($"Invalid value for {name}.");
        }
    }

    public static bool IsReserved(string name) => name.StartsWith("XODUS_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("XDG_", StringComparison.OrdinalIgnoreCase)
        || new[] { "HOME", "DBUS_SESSION_BUS_ADDRESS", "WINEPREFIX", "WINESERVER", "WINELOADER", "WINEARCH", "WINEDLLPATH", "WINE_DLL_FILE_MAP" }
            .Contains(name, StringComparer.OrdinalIgnoreCase);
}

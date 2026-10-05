namespace Orion.Domain;

public enum MangoHudPosition { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>Optional, per-instance HUD. No installation or system configuration changes.</summary>
public sealed record MangoHudOptions
{
    public bool Enabled { get; init; } = true;
    // null follows detection until the user explicitly chooses per-instance or system settings.
    public bool? UseSystemConfig { get; init; }
    public bool Fps { get; init; } = true;
    public bool FrameTime { get; init; }
    public bool Cpu { get; init; } = true;
    public bool Gpu { get; init; } = true;
    public bool Ram { get; init; } = true;
    public bool Vram { get; init; }
    public bool CpuTemperature { get; init; }
    public bool GpuTemperature { get; init; }
    public bool Battery { get; init; }
    public bool Resolution { get; init; }
    public MangoHudPosition Position { get; init; }

    public void Validate()
    {
        if (!Enum.IsDefined(Position)) throw new ArgumentException("Invalid MangoHud position.");
    }
}

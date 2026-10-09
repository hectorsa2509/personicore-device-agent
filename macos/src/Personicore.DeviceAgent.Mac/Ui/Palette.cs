using Avalonia.Media;
using Personicore.DeviceAgent.Agent;

namespace Personicore.DeviceAgent.Mac.Ui;

/// <summary>House palette, the same values as the Windows agent and the HTML deliverables.</summary>
internal static class Palette
{
    public static readonly Color Paper = Color.FromRgb(0xF7, 0xF4, 0xED);
    public static readonly Color PaperSunken = Color.FromRgb(0xEE, 0xEA, 0xE0);
    public static readonly Color Ink = Color.FromRgb(0x14, 0x2E, 0x28);
    public static readonly Color InkSoft = Color.FromRgb(0x5A, 0x6B, 0x64);
    public static readonly Color Line = Color.FromRgb(0xD8, 0xD2, 0xC4);
    public static readonly Color OnInk = Color.FromRgb(0xB9, 0xC7, 0xC1);

    public static readonly Color Signal = Color.FromRgb(0xE0, 0x5A, 0x2B);
    public static readonly Color Good = Color.FromRgb(0x2E, 0x7D, 0x5B);
    public static readonly Color Info = Color.FromRgb(0x2C, 0x6E, 0x9B);
    public static readonly Color Bad = Color.FromRgb(0xB4, 0x33, 0x2A);
    public static readonly Color Idle = Color.FromRgb(0x8A, 0x92, 0x8D);

    public static IBrush Brush(Color color) => new SolidColorBrush(color);

    public static Color For(AgentStatus status) => status switch
    {
        AgentStatus.Registered => Good,
        AgentStatus.Simulated => Info,
        AgentStatus.OwnerRequired => Signal,
        // Waiting on Personicore support, not on the panelist: worth noticing, not alarming.
        AgentStatus.HouseholdNotAssigned or AgentStatus.BackendNotReady => Signal,
        AgentStatus.Error => Bad,
        AgentStatus.RouterNotFound => Idle,
        _ => Info,
    };
}

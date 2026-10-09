using System.Drawing;

namespace Personicore.DeviceAgent.Ui;

/// <summary>
/// The house palette (Personicore / Personicore) carried over to WinForms: bone paper, pine
/// ink and the three signal accents. The same values the HTML deliverables use, so the
/// agent's windows do not look like they come from another family.
/// </summary>
public static class Palette
{
    public static readonly Color Paper = Color.FromArgb(0xF7, 0xF4, 0xED);
    public static readonly Color PaperSunken = Color.FromArgb(0xEE, 0xEA, 0xE0);
    public static readonly Color Ink = Color.FromArgb(0x14, 0x2E, 0x28);
    public static readonly Color InkSoft = Color.FromArgb(0x5A, 0x6B, 0x64);
    public static readonly Color Line = Color.FromArgb(0xD8, 0xD2, 0xC4);

    /// <summary>Secondary text on an ink background: the subtitle under a window heading.</summary>
    public static readonly Color OnInk = Color.FromArgb(0xB9, 0xC7, 0xC1);

    public static readonly Color Signal = Color.FromArgb(0xE0, 0x5A, 0x2B);   // naranja
    public static readonly Color Good = Color.FromArgb(0x2E, 0x7D, 0x5B);     // verde
    public static readonly Color Info = Color.FromArgb(0x2C, 0x6E, 0x9B);     // azul
    public static readonly Color Bad = Color.FromArgb(0xB4, 0x33, 0x2A);      // rojo
    public static readonly Color Idle = Color.FromArgb(0x8A, 0x92, 0x8D);     // gris
}

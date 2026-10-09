using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Personicore.DeviceAgent.Agent;
using Personicore.DeviceAgent.Config;

namespace Personicore.DeviceAgent.Mac.Ui;

/// <summary>
/// The Personicore mark tinted with the status color, as on Windows: the shape never
/// changes, the color says the status. Built from the white silhouettes that
/// windows/build/make-icon.py generates.
/// </summary>
internal static class MarkIcons
{
    private static readonly Dictionary<(AgentStatus, int), WindowIcon> Cache = [];

    /// <summary>Menu bar icon. 40 px renders crisp at the ~18 pt menu bar height on Retina.</summary>
    public static WindowIcon ForMenuBar(AgentStatus status) => For(status, 40);

    public static WindowIcon ForWindow(AgentStatus status) => For(status, 64);

    public static Bitmap Bitmap(AgentStatus status, int size) => Tinted(status, size);

    private static WindowIcon For(AgentStatus status, int size)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((status, size), out var cached)) return cached;
            var icon = new WindowIcon(Tinted(status, size));
            Cache[(status, size)] = icon;
            return icon;
        }
    }

    private static WriteableBitmap Tinted(AgentStatus status, int size)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"mark-{size}.png")
                           ?? throw new InvalidOperationException($"Embedded resource mark-{size}.png is missing.");

        var bitmap = WriteableBitmap.DecodeToWidth(stream, size);
        var color = Palette.For(status);

        using var buffer = bitmap.Lock();
        var pixels = new int[buffer.Size.Width * buffer.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);

        // Keep each pixel's alpha, replace its color. The buffer is premultiplied, so the
        // color channels are scaled by alpha; byte order follows the buffer's format.
        var bgra = buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888;
        for (var i = 0; i < pixels.Length; i++)
        {
            var alpha = (pixels[i] >>> 24) & 0xFF;
            if (alpha == 0) { pixels[i] = 0; continue; }

            int Scale(byte c) => c * alpha / 255;
            var (first, third) = bgra ? (Scale(color.B), Scale(color.R)) : (Scale(color.R), Scale(color.B));
            pixels[i] = (alpha << 24) | (third << 16) | (Scale(color.G) << 8) | first;
        }

        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        return bitmap;
    }
}

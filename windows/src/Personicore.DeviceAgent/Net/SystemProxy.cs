using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Personicore.DeviceAgent.Net;

/// <summary>
/// A snapshot of the user's proxy settings for the LAN connection.
/// </summary>
/// <param name="Flags">PROXY_TYPE_* bits (direct, manual proxy, PAC URL, auto-detect).</param>
/// <param name="Server">Manual proxy, e.g. "192.168.0.190:3129". Kept even when disabled.</param>
/// <param name="Bypass">Addresses that skip the proxy, separated by ';'.</param>
public sealed record ProxySettings(int Flags, string Server, string Bypass)
{
    public bool ManualEnabled => (Flags & SystemProxy.ProxyTypeProxy) != 0;
}

/// <summary>
/// The per-user Windows proxy — the one shown in Settings → Network &amp; internet → Proxy →
/// Manual proxy setup, and used by Edge, Chrome and most apps.
///
/// It is written through WinINet's per-connection options, not by poking the registry:
/// the API updates both the Internet Settings values and the DefaultConnectionSettings
/// blob that the Settings page reads, and tells running apps to reload. No administrator
/// rights are needed, because it is the current user's setting.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SystemProxy
{
    public const int ProxyTypeDirect = 0x1;
    public const int ProxyTypeProxy = 0x2;

    /// <summary>
    /// Local and private addresses skip the proxy: the meter node itself, printers, the
    /// ISP router's page. "&lt;local&gt;" is the "Don't use the proxy server for local
    /// (intranet) addresses" checkbox in Settings.
    /// </summary>
    public static readonly string DefaultBypass = string.Join(';',
        new[] { "<local>", "localhost", "127.*", "10.*", "192.168.*", "169.254.*" }
            .Concat(Enumerable.Range(16, 16).Select(i => $"172.{i}.*")));

    private const int InternetOptionRefresh = 37;
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionPerConnectionOption = 75;

    private const int PerConnFlags = 1;
    private const int PerConnProxyServer = 2;
    private const int PerConnProxyBypass = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PerConnOptionList
    {
        public int Size;
        public IntPtr Connection;   // NULL = the LAN connection
        public int OptionCount;
        public int OptionError;
        public IntPtr Options;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PerConnValue
    {
        [FieldOffset(0)] public int Number;
        [FieldOffset(0)] public IntPtr Text;
        [FieldOffset(0)] public long FileTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerConnOption
    {
        public int Option;
        public PerConnValue Value;
    }

    [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "InternetSetOptionW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int length);

    [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "InternetQueryOptionW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetQueryOption(IntPtr internet, int option, IntPtr buffer, ref int length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    /// <summary>Current settings of the LAN connection.</summary>
    public static ProxySettings Read()
    {
        int[] ids = [PerConnFlags, PerConnProxyServer, PerConnProxyBypass];
        var optionSize = Marshal.SizeOf<PerConnOption>();
        var options = Marshal.AllocHGlobal(optionSize * ids.Length);
        var list = IntPtr.Zero;

        try
        {
            for (var i = 0; i < ids.Length; i++)
                Marshal.StructureToPtr(new PerConnOption { Option = ids[i] }, options + i * optionSize, false);

            var header = new PerConnOptionList
            {
                Size = Marshal.SizeOf<PerConnOptionList>(),
                OptionCount = ids.Length,
                Options = options,
            };

            var length = header.Size;
            list = Marshal.AllocHGlobal(length);
            Marshal.StructureToPtr(header, list, false);

            if (!InternetQueryOption(IntPtr.Zero, InternetOptionPerConnectionOption, list, ref length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InternetQueryOption failed");

            var flags = Marshal.PtrToStructure<PerConnOption>(options).Value.Number;
            var server = TakeString(Marshal.PtrToStructure<PerConnOption>(options + optionSize).Value.Text);
            var bypass = TakeString(Marshal.PtrToStructure<PerConnOption>(options + 2 * optionSize).Value.Text);

            return new ProxySettings(flags, server, bypass);
        }
        finally
        {
            if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
            Marshal.FreeHGlobal(options);
        }
    }

    /// <summary>
    /// Writes the settings and notifies running apps, so browsers and the Settings page
    /// see the change without a restart. Throws if Windows refuses (e.g. a group policy
    /// that locks the proxy).
    /// </summary>
    public static void Write(ProxySettings settings)
    {
        var optionSize = Marshal.SizeOf<PerConnOption>();
        var options = Marshal.AllocHGlobal(optionSize * 3);
        var server = Marshal.StringToHGlobalUni(settings.Server);
        var bypass = Marshal.StringToHGlobalUni(settings.Bypass);
        var list = IntPtr.Zero;

        try
        {
            Marshal.StructureToPtr(new PerConnOption { Option = PerConnFlags, Value = new PerConnValue { Number = settings.Flags } }, options, false);
            Marshal.StructureToPtr(new PerConnOption { Option = PerConnProxyServer, Value = new PerConnValue { Text = server } }, options + optionSize, false);
            Marshal.StructureToPtr(new PerConnOption { Option = PerConnProxyBypass, Value = new PerConnValue { Text = bypass } }, options + 2 * optionSize, false);

            var header = new PerConnOptionList
            {
                Size = Marshal.SizeOf<PerConnOptionList>(),
                OptionCount = 3,
                Options = options,
            };

            list = Marshal.AllocHGlobal(header.Size);
            Marshal.StructureToPtr(header, list, false);

            if (!InternetSetOption(IntPtr.Zero, InternetOptionPerConnectionOption, list, header.Size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows refused the proxy change");

            InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
        finally
        {
            if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
            Marshal.FreeHGlobal(options);
            Marshal.FreeHGlobal(server);
            Marshal.FreeHGlobal(bypass);
        }
    }

    /// <summary>Strings returned by InternetQueryOption are allocated by WinINet with GlobalAlloc.</summary>
    private static string TakeString(IntPtr text)
    {
        if (text == IntPtr.Zero) return string.Empty;
        try
        {
            return Marshal.PtrToStringUni(text) ?? string.Empty;
        }
        finally
        {
            GlobalFree(text);
        }
    }
}

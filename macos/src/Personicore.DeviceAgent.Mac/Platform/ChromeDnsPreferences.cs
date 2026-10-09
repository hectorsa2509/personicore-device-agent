using System.Runtime.InteropServices;

namespace Personicore.DeviceAgent.Mac.Platform;

/// <summary>
/// Chrome's documented macOS preference-policy domain. CFPreferences keeps cfprefsd
/// coherent; editing Chrome's live JSON or managed plist files would not.
/// </summary>
internal sealed class ChromeDnsPreferences : IChromeDnsPreferences
{
    private const string Framework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly IntPtr Library = NativeLibrary.Load(Framework);
    private static IntPtr Symbol(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(Library, name));

    public bool IsForced => WithKey((key, domain) => CFPreferencesAppValueIsForced(key, domain));
    public string? ReadEffective() => Read(false);
    public string? ReadLocal() => Read(true);

    private string? Read(bool local) => WithKey((key, domain) =>
    {
        var value = local
            ? CFPreferencesCopyValue(key, domain, Symbol("kCFPreferencesCurrentUser"), Symbol("kCFPreferencesAnyHost"))
            : CFPreferencesCopyAppValue(key, domain);
        if (value == IntPtr.Zero) return null;
        try
        {
            // Unexpected types are existing configuration, never permission to replace it.
            if (CFGetTypeID(value) != CFStringGetTypeID()) return "(non-string policy)";
            var buffer = new byte[4096];
            if (!CFStringGetCString(value, buffer, buffer.Length, 0x08000100))
                throw new IOException("Chrome preference could not be decoded.");
            return System.Text.Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0));
        }
        finally { CFRelease(value); }
    });

    public void WriteLocal(string? value) => WithKey((key, domain) =>
    {
        var text = value is null ? IntPtr.Zero : String(value);
        try
        {
            CFPreferencesSetValue(key, text, domain, Symbol("kCFPreferencesCurrentUser"), Symbol("kCFPreferencesAnyHost"));
            if (!CFPreferencesSynchronize(domain, Symbol("kCFPreferencesCurrentUser"), Symbol("kCFPreferencesAnyHost")))
                throw new IOException("Chrome preference could not be synchronized.");
            return true;
        }
        finally { if (text != IntPtr.Zero) CFRelease(text); }
    });

    private static T WithKey<T>(Func<IntPtr, IntPtr, T> action)
    {
        var key = String("DnsOverHttpsMode");
        var domain = String("com.google.Chrome");
        try { return action(key, domain); }
        finally { CFRelease(key); CFRelease(domain); }
    }

    private static IntPtr String(string text) => CFStringCreateWithCString(IntPtr.Zero, text, 0x08000100);
    [DllImport(Framework)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(Framework)] private static extern void CFRelease(IntPtr value);
    [DllImport(Framework)] private static extern nuint CFGetTypeID(IntPtr value);
    [DllImport(Framework)] private static extern nuint CFStringGetTypeID();
    [DllImport(Framework)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr value, byte[] buffer, nint size, uint encoding);
    [DllImport(Framework)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFPreferencesAppValueIsForced(IntPtr key, IntPtr application);
    [DllImport(Framework)] private static extern IntPtr CFPreferencesCopyAppValue(IntPtr key, IntPtr application);
    [DllImport(Framework)] private static extern IntPtr CFPreferencesCopyValue(IntPtr key, IntPtr application, IntPtr user, IntPtr host);
    [DllImport(Framework)] private static extern void CFPreferencesSetValue(IntPtr key, IntPtr value, IntPtr application, IntPtr user, IntPtr host);
    [DllImport(Framework)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFPreferencesSynchronize(IntPtr application, IntPtr user, IntPtr host);
}

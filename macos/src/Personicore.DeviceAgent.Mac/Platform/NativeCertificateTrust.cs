using System.Runtime.InteropServices;

namespace Personicore.DeviceAgent.Mac.Platform;

/// <summary>Security.framework authorization stays in the interactive agent process.</summary>
internal static class NativeCertificateTrust
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint AdminDomain = 1;

    public static void Authorize(byte[] der)
    {
        var certificate = CreateCertificate(der);
        try
        {
            // NULL is Apple's documented unrestricted Always Trust setting for a root.
            // Even an existing certificate must go through this step: import != trust.
            Check(SecTrustSettingsSetTrustSettings(certificate, AdminDomain, IntPtr.Zero), "Authorize Always Trust");
        }
        finally
        {
            CFRelease(certificate);
        }
    }

    public static bool HasAlwaysTrust(byte[] der)
    {
        var certificate = CreateCertificate(der);
        IntPtr settings = IntPtr.Zero;
        try
        {
            // NULL supplied to Set is stored as an empty array, not a missing record.
            return SecTrustSettingsCopyTrustSettings(certificate, AdminDomain, out settings) == 0
                && settings != IntPtr.Zero && CFArrayGetCount(settings) == 0;
        }
        finally
        {
            if (settings != IntPtr.Zero) CFRelease(settings);
            CFRelease(certificate);
        }
    }

    private static IntPtr CreateCertificate(byte[] der)
    {
        var data = CFDataCreate(IntPtr.Zero, der, der.Length);
        if (data == IntPtr.Zero) throw new InvalidOperationException("Could not allocate certificate data.");
        try
        {
            var certificate = SecCertificateCreateWithData(IntPtr.Zero, data);
            return certificate != IntPtr.Zero ? certificate
                : throw new InvalidOperationException("Security.framework rejected the certificate.");
        }
        finally { CFRelease(data); }
    }

    private static void Check(int status, string operation)
    {
        if (status == 0) return;
        throw new InvalidOperationException($"{operation} failed (OSStatus {status}). "
            + "Complete the macOS authorization dialogs or ask your administrator to configure certificate trust.");
    }

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr value);
    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(Security)]
    private static extern IntPtr SecCertificateCreateWithData(IntPtr allocator, IntPtr data);
    [DllImport(Security)]
    private static extern int SecTrustSettingsSetTrustSettings(IntPtr certificate, uint domain, IntPtr settings);
    [DllImport(Security)]
    private static extern int SecTrustSettingsCopyTrustSettings(IntPtr certificate, uint domain, out IntPtr settings);
}

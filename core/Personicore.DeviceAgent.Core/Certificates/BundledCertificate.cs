using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Personicore.DeviceAgent.Certificates;

public static class BundledCertificate
{
    public const string Sha256 = "741FFDF0F0CBD5E75255B797F4208BCED3D1C27181DFC5FB2120F33AD231D94C";

    public static X509Certificate2 Load()
    {
        using var stream = typeof(BundledCertificate).Assembly.GetManifestResourceStream("personicore-ca.crt")
            ?? throw new InvalidOperationException("Bundled CA certificate is missing.");
        using var reader = new StreamReader(stream);
        var pem = reader.ReadToEnd();
        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new InvalidOperationException("The bundled CA must contain only a public certificate.");
        var certificate = X509Certificate2.CreateFromPem(pem);
        if (certificate.GetCertHashString(HashAlgorithmName.SHA256) != Sha256
            || !certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(x => x.CertificateAuthority)
            || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime()
            || DateTime.UtcNow > certificate.NotAfter.ToUniversalTime())
        {
            certificate.Dispose();
            throw new InvalidOperationException("Bundled CA identity or validity check failed.");
        }
        return certificate;
    }
}

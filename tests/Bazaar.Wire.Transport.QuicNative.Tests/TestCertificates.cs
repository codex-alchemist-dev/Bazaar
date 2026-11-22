using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Bazaar.Wire.Transport.QuicNative.Tests;

internal static class TestCertificates
{
    /// <summary>A throwaway self-signed ECDSA certificate for localhost; the key never leaves the test process.</summary>
    public static X509Certificate2 CreateLocalhost()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Same recipe as tests/Bazaar.Conduit.Tests: Windows Schannel cannot serve with an ephemeral key, so round-trip it into the user key store.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, string.Empty), string.Empty, X509KeyStorageFlags.UserKeySet);
    }
}

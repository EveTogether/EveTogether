using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EveUtils.Server.Transport;

/// <summary>
/// Generates a self-signed TLS certificate on first start and persists it as a PFX in the server
/// data folder. The client pins the SHA-256 fingerprint during pairing
/// (TOFU). No CA, no domain. Exposes the fingerprint for the admin panel + out-of-band verification.
/// </summary>
public sealed class SelfSignedCertProvider(string dataDirectory)
{
    private readonly string _pfxPath = Path.Combine(dataDirectory, "server-cert.pfx");

    // Windows/Schannel can't use an ephemeral private key for server-side TLS — the key isn't in a
    // keystore Schannel can reach, so the handshake fails before ServerHello. Persist it on Windows
    // (UserKeySet, no admin needed); keep ephemeral elsewhere where OpenSSL handles it fine.
    private static X509KeyStorageFlags KeyStorageFlags => OperatingSystem.IsWindows()
        ? X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet
        : X509KeyStorageFlags.EphemeralKeySet;

    public X509Certificate2 GetOrCreate()
    {
        Directory.CreateDirectory(dataDirectory);

        if (File.Exists(_pfxPath))
            return X509CertificateLoader.LoadPkcs12FromFile(_pfxPath, null, KeyStorageFlags);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=eve-utils-server", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // serverAuth

        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var pfx = cert.Export(X509ContentType.Pfx);
        File.WriteAllBytes(_pfxPath, pfx);
        TryRestrictPermissions(_pfxPath);

        return X509CertificateLoader.LoadPkcs12(pfx, null, KeyStorageFlags);
    }

    public static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (IOException)
            {
            }
        }
    }
}

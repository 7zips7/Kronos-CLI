using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KronosCLI;

internal sealed class CertificateAuthority(X509Certificate2 root, RSA key) : IDisposable
{
    const string FiddlerRoot = "CN=DO_NOT_TRUST_FiddlerRoot";
    const string KeyName = "KronosCLI.Proxy";

    readonly ConcurrentDictionary<string, Lazy<X509Certificate2>> issued = new(StringComparer.OrdinalIgnoreCase);

    public static CertificateAuthority? FromFiddler()
    {
        using var trusted = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        using var personal = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        trusted.Open(OpenFlags.ReadOnly);
        personal.Open(OpenFlags.ReadOnly);

        var root = personal.Certificates
            .Where(c => c.Subject.StartsWith(FiddlerRoot, StringComparison.Ordinal) && c.HasPrivateKey && c.NotAfter > DateTime.Now)
            .Where(c => trusted.Certificates.Find(X509FindType.FindByThumbprint, c.Thumbprint, false).Count > 0)
            .MaxBy(c => c.NotBefore);
        return root == null ? null : new CertificateAuthority(root, OpenKey());
    }

    static RSA OpenKey()
    {
        try
        {
            if (CngKey.Exists(KeyName)) return new RSACng(CngKey.Open(KeyName));
            return new RSACng(CngKey.Create(CngAlgorithm.Rsa, KeyName, new CngKeyCreationParameters
            {
                Parameters = { new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None) },
            }));
        }
        catch (CryptographicException) when (CngKey.Exists(KeyName))
        {
            return new RSACng(CngKey.Open(KeyName));
        }
    }

    public X509Certificate2 For(string host) =>
        issued.GetOrAdd(host, h => new Lazy<X509Certificate2>(() => Issue(h))).Value;

    X509Certificate2 Issue(string host)
    {
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var names = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var address)) names.AddIpAddress(address);
        else names.AddDnsName(host);

        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));

        var now = DateTime.Now;
        var notBefore = now.AddDays(-1) < root.NotBefore ? root.NotBefore : now.AddDays(-1);
        var notAfter = now.AddDays(365) > root.NotAfter ? root.NotAfter : now.AddDays(365);

        using var certificate = request.Create(root, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
        return certificate.CopyWithPrivateKey(key);
    }

    public void Dispose()
    {
        foreach (var certificate in issued.Values.Where(c => c.IsValueCreated)) certificate.Value.Dispose();
        root.Dispose();
        key.Dispose();
    }
}

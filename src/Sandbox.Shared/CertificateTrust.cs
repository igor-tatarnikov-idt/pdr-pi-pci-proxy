using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Sandbox.Shared;

/// <summary>
/// Trust of a provider's certificate by explicit path, shared by the adapters and the PCI Proxy: the sandbox's
/// self-signed certificate is the one custom root, and the system trust store plays no part.
/// </summary>
public static class CertificateTrust
{
    /// <summary>
    /// Loads the certificate at <paramref name="configuredPath"/> (relative to <paramref name="contentRoot"/>), or returns
    /// <c>null</c> when no path is configured. A path to a file that does not exist is an error that says how to create it.
    /// </summary>
    public static X509Certificate2? Load(string? configuredPath, string contentRoot)
    {
        if (string.IsNullOrEmpty(configuredPath))
        {
            return null;
        }

        var path = Path.GetFullPath(configuredPath, contentRoot);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Provider:TrustedCertificate points to '{path}', which does not exist. Run scripts/gen-cert.sh to create it.", path);
        }
        return X509CertificateLoader.LoadCertificateFromFile(path);
    }

    /// <summary>
    /// The <see cref="SslClientAuthenticationOptions.RemoteCertificateValidationCallback"/> decision: the presented
    /// certificate is accepted only if its host name matches the request's and it chains to <paramref name="trusted"/>
    /// as the sole custom root (no revocation check).
    /// </summary>
    public static bool IsTrusted(X509Certificate2 trusted, X509Certificate? presented, SslPolicyErrors errors)
    {
        // The name is checked by the TLS stack; the chain below is built by us, so the system trust store plays no part.
        if (presented is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
            || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return false;
        }

        using var leaf = new X509Certificate2(presented);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trusted);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    }
}

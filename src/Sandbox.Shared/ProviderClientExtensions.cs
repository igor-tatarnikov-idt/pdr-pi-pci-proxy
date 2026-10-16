using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Sandbox.Shared;

/// <summary>How an adapter reaches its provider: <see cref="Off"/> directly, <see cref="Egress"/> through the Forwarder.</summary>
public enum PciProxyMode { Off, Egress }

public static class ProviderClientExtensions
{
    /// <summary>
    /// Reads <c>PciProxy:Mode</c> (<c>off</c> or <c>egress</c>, any casing). Any other value, an empty one included,
    /// prints a message to stderr and ends the process with exit code 1: the mode never falls back to <c>off</c>.
    /// </summary>
    public static PciProxyMode GetPciProxyMode(this IConfiguration configuration)
    {
        var value = configuration["PciProxy:Mode"];
        if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            return PciProxyMode.Off;
        }
        if (string.Equals(value, "egress", StringComparison.OrdinalIgnoreCase))
        {
            return PciProxyMode.Egress;
        }
        ExitWithError($"PciProxy:Mode must be 'off' or 'egress' (got '{value}')");
        throw new UnreachableException();
    }

    /// <summary>Logs the mode at startup: <c>PciProxy mode=off</c>, or <c>PciProxy mode=egress forwarder=&lt;url&gt;</c>.</summary>
    public static void LogPciProxyMode(this ILogger logger, PciProxyMode mode, IConfiguration configuration)
    {
        if (mode == PciProxyMode.Egress)
        {
            logger.LogInformation("PciProxy mode=egress forwarder={Forwarder}", configuration["PciProxy:ForwarderUrl"]);
        }
        else
        {
            logger.LogInformation("PciProxy mode=off");
        }
    }

    /// <summary>
    /// Makes the provider client's primary handler a <see cref="SocketsHttpHandler"/>. In <c>egress</c> mode every
    /// request goes through the Forwarder at <c>PciProxy:ForwarderUrl</c> (plain HTTP as an absolute-URI request,
    /// HTTPS through a <c>CONNECT</c> tunnel); in <c>off</c> mode the client connects directly and ignores the
    /// <c>http_proxy</c> / <c>https_proxy</c> environment variables. When <c>Provider:TrustedCertificate</c> is set, the provider's
    /// certificate is accepted only if it chains to that one certificate as the sole custom root (no revocation check)
    /// and its host name matches the request's.
    /// </summary>
    public static IHttpClientBuilder ConfigureProviderHandler(this IHttpClientBuilder builder, IConfiguration configuration, string contentRoot)
    {
        var trusted = LoadTrustedCertificate(configuration["Provider:TrustedCertificate"], contentRoot);
        var forwarder = configuration.GetPciProxyMode() == PciProxyMode.Egress ? GetForwarderUri(configuration) : null;
        return builder.ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new SocketsHttpHandler();
            if (forwarder is not null)
            {
                handler.Proxy = new WebProxy(forwarder);
                handler.UseProxy = true;
            }
            else
            {
                // 'off' means direct: ignore http_proxy / https_proxy / no_proxy, which would otherwise catch the
                // *.localhost provider names (no_proxy entries are matched exactly).
                handler.UseProxy = false;
            }
            if (trusted is not null)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) => IsTrusted(trusted, certificate, errors);
            }
            return handler;
        });
    }

    // Without a valid address a proxy is silently not used, so a bad URL must stop the app instead.
    private static Uri GetForwarderUri(IConfiguration configuration)
    {
        var value = configuration["PciProxy:ForwarderUrl"];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            ExitWithError($"PciProxy:ForwarderUrl must be an absolute http URL when PciProxy:Mode is 'egress' (got '{value}')");
        }
        return uri;
    }

    [DoesNotReturn]
    private static void ExitWithError(string message)
    {
        Console.Error.WriteLine(message);
        Environment.Exit(1);
    }

    private static X509Certificate2? LoadTrustedCertificate(string? configuredPath, string contentRoot)
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

    private static bool IsTrusted(X509Certificate2 trusted, X509Certificate? presented, SslPolicyErrors errors)
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

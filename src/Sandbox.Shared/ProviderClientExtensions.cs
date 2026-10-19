using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sandbox.Shared;

/// <summary>
/// How an adapter reaches its provider: <see cref="Off"/> directly, <see cref="Egress"/> through the Forwarder, <see cref="On"/>
/// through the PCI Proxy for a request that carries a marker and through the Forwarder for any other.
/// </summary>
public enum PciProxyMode { Off, Egress, On }

public static class ProviderClientExtensions
{
    /// <summary>
    /// Reads <c>PciProxy:Mode</c> (<c>off</c>, <c>egress</c> or <c>on</c>, any casing). Any other value, an empty one included,
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
        if (string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            return PciProxyMode.On;
        }
        ExitWithError($"PciProxy:Mode must be 'off', 'egress' or 'on' (got '{value}')");
        throw new UnreachableException();
    }

    /// <summary>
    /// Logs the mode at startup: <c>PciProxy mode=off</c>, <c>PciProxy mode=egress forwarder=&lt;url&gt;</c> or
    /// <c>PciProxy mode=on proxy=&lt;url&gt; tenant=&lt;tenant&gt; provider=&lt;id&gt; forwarder=&lt;url&gt;</c>.
    /// </summary>
    public static void LogPciProxyMode(this ILogger logger, PciProxyMode mode, IConfiguration configuration)
    {
        switch (mode)
        {
            case PciProxyMode.On:
                logger.LogInformation("PciProxy mode=on proxy={Proxy} tenant={Tenant} provider={Provider} forwarder={Forwarder}",
                    configuration["PciProxy:ProxyUrl"], configuration["PciProxy:TenantId"], configuration["PciProxy:ProviderId"], configuration["PciProxy:ForwarderUrl"]);
                break;
            case PciProxyMode.Egress:
                logger.LogInformation("PciProxy mode=egress forwarder={Forwarder}", configuration["PciProxy:ForwarderUrl"]);
                break;
            default:
                logger.LogInformation("PciProxy mode=off");
                break;
        }
    }

    /// <summary>
    /// Makes the provider client's primary handler a <see cref="SocketsHttpHandler"/>. In <c>egress</c> mode every
    /// request goes through the Forwarder at <c>PciProxy:ForwarderUrl</c> (plain HTTP as an absolute-URI request,
    /// HTTPS through a <c>CONNECT</c> tunnel), and in <c>on</c> mode every request that reaches this handler does
    /// (<see cref="AddPciProxy"/> takes the ones with a marker); in <c>off</c> mode the client connects directly and ignores the
    /// <c>http_proxy</c> / <c>https_proxy</c> environment variables. When <c>Provider:TrustedCertificate</c> is set, the provider's
    /// certificate is accepted only if it chains to that one certificate as the sole custom root (no revocation check)
    /// and its host name matches the request's.
    /// </summary>
    public static IHttpClientBuilder ConfigureProviderHandler(this IHttpClientBuilder builder, IConfiguration configuration, string contentRoot)
    {
        var trusted = CertificateTrust.Load(configuration["Provider:TrustedCertificate"], contentRoot);
        var mode = configuration.GetPciProxyMode();
        var forwarder = mode == PciProxyMode.Off ? null : GetForwarderUri(configuration, mode);
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
                handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) => CertificateTrust.IsTrusted(trusted, certificate, errors);
            }
            return handler;
        });
    }

    /// <summary>
    /// In <c>on</c> mode, sends every request that carries a marker (in its URL, a header or its body) to the PCI Proxy at
    /// <c>PciProxy:ProxyUrl</c>, as <c>/api/t/&lt;TenantId&gt;/providers/&lt;ProviderId&gt;/send</c>, instead of to its URL: see
    /// <see cref="PciProxyHandler"/>. The proxy is reached on its own client, <c>pciproxy</c>, which never uses the Forwarder.
    /// <c>PciProxy:ProxyUrl</c> (an absolute http or https URL), <c>PciProxy:TenantId</c> and <c>PciProxy:ProviderId</c> must
    /// be valid, or the process ends with exit code 1. In any other mode this does nothing.
    /// </summary>
    public static IHttpClientBuilder AddPciProxy(this IHttpClientBuilder builder, IConfiguration configuration)
    {
        if (configuration.GetPciProxyMode() != PciProxyMode.On)
        {
            return builder;
        }

        var proxy = configuration["PciProxy:ProxyUrl"];
        if (!Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri) || (proxyUri.Scheme != Uri.UriSchemeHttp && proxyUri.Scheme != Uri.UriSchemeHttps))
        {
            ExitWithError($"PciProxy:ProxyUrl must be an absolute http or https URL when PciProxy:Mode is 'on' (got '{proxy}')");
        }
        var tenant = RequireId(configuration, "TenantId");
        var provider = RequireId(configuration, "ProviderId");
        var sendUri = new Uri($"{proxyUri.AbsoluteUri.TrimEnd('/')}/api/t/{tenant}/providers/{provider}/send");

        builder.Services.AddHttpClient(PciProxyHandler.ClientName)
            .AddOutboundLogging(configuration[HostDefaults.ApplicationKey]!)
            // Never the Forwarder or http_proxy. The proxy's answer, a provider's 3xx included, is the adapter's answer: no redirect is
            // followed (it would send the marker-bearing request on to the Location, direct) and no cookie is kept or replayed.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false });
        return builder.AddHttpMessageHandler(sp => new PciProxyHandler(sp.GetRequiredService<IHttpClientFactory>(), sendUri));
    }

    // The id as it goes into the path of the proxy's URL.
    private static string RequireId(IConfiguration configuration, string key)
    {
        var value = configuration[$"PciProxy:{key}"];
        if (string.IsNullOrWhiteSpace(value))
        {
            ExitWithError($"PciProxy:{key} must not be empty when PciProxy:Mode is 'on'");
        }
        return Uri.EscapeDataString(value);
    }

    // Without a valid address a proxy is silently not used, so a bad URL must stop the app instead.
    private static Uri GetForwarderUri(IConfiguration configuration, PciProxyMode mode)
    {
        var value = configuration["PciProxy:ForwarderUrl"];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            ExitWithError($"PciProxy:ForwarderUrl must be an absolute http URL when PciProxy:Mode is '{mode.ToString().ToLowerInvariant()}' (got '{value}')");
        }
        return uri;
    }

    [DoesNotReturn]
    private static void ExitWithError(string message)
    {
        Console.Error.WriteLine(message);
        Environment.Exit(1);
    }
}

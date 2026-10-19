using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sandbox.Shared;

namespace PciProxy;

/// <summary>A provider's own wire format: changes the request body once the markers in it are replaced.</summary>
public interface IRequestHook
{
    /// <summary>The body to send and its <c>Content-Type</c>, from the body with the raw values in it and the <c>Content-Type</c> it came with.</summary>
    (byte[] Body, string ContentType) Apply(byte[] body, string? contentType);
}

/// <summary>
/// The <c>encrypt</c> hook: the body, as UTF-8 text, becomes <c>{"payload":"&lt;PayloadCipher.Encrypt(body)&gt;"}</c>,
/// sent as <c>application/json</c>.
/// </summary>
public sealed class EncryptHook : IRequestHook
{
    // Base64 contains '+'; relaxed escaping keeps it as '+', as the EncryptedAdapter writes its envelope.
    private static readonly JsonSerializerOptions EnvelopeOptions =
        new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public (byte[] Body, string ContentType) Apply(byte[] body, string? contentType) =>
        (JsonSerializer.SerializeToUtf8Bytes(new EncryptedEnvelope(PayloadCipher.Encrypt(Encoding.UTF8.GetString(body))), EnvelopeOptions),
            "application/json");
}

/// <summary>What the pipeline needs to send to one provider: the client that connects to it and the hook, if any.</summary>
public sealed record ProviderProfile(HttpClient Client, IRequestHook? Hook);

/// <summary>
/// The profiles of the <c>Providers</c> configuration, keyed by provider id (any casing). An id without an entry is the plain
/// profile: system trust and no hook. An entry has an optional <c>Hook</c> (<c>encrypt</c>) and an optional
/// <c>TrustedCertificate</c>, which that provider's connections accept as the only root, as the adapters do.
/// </summary>
public sealed class ProviderProfiles
{
    private const string DefaultClientName = "provider";

    private static readonly Dictionary<string, IRequestHook> KnownHooks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["encrypt"] = new EncryptHook(),
    };

    private readonly IReadOnlyDictionary<string, Entry> _entries;
    private readonly IHttpClientFactory _clients;

    private sealed record Entry(string ClientName, IRequestHook? Hook);

    private ProviderProfiles(IReadOnlyDictionary<string, Entry> entries, IHttpClientFactory clients)
    {
        _entries = entries;
        _clients = clients;
    }

    public ProviderProfile Get(string providerId) =>
        _entries.TryGetValue(providerId, out var entry)
            ? new ProviderProfile(_clients.CreateClient(entry.ClientName), entry.Hook)
            : new ProviderProfile(_clients.CreateClient(DefaultClientName), null);

    /// <summary>
    /// Reads <c>Providers</c>, registers one named client for the default profile and one for each entry with a
    /// <c>TrustedCertificate</c>, and registers the profiles. An unknown <c>Hook</c> or a certificate that does not exist
    /// prints why on stderr and ends the process with exit code 1, before anything listens.
    /// </summary>
    public static void Add(IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        AddProviderClient(services, DefaultClientName, trusted: null);

        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in configuration.GetSection("Providers").GetChildren())
        {
            var id = section.Key;
            var hook = ReadHook(id, section["Hook"]);
            var trusted = ReadCertificate(id, section["TrustedCertificate"], contentRoot);
            var clientName = DefaultClientName;
            if (trusted is not null)
            {
                clientName = $"{DefaultClientName}:{id}";
                AddProviderClient(services, clientName, trusted);
            }
            entries[id] = new Entry(clientName, hook);
        }

        services.AddSingleton(provider => new ProviderProfiles(entries, provider.GetRequiredService<IHttpClientFactory>()));
    }

    // A provider's client connects directly (never through the Forwarder or a system proxy) and keeps no cookies: the proxy
    // stores nothing between requests. One request is bounded by X-Proxy-TimeoutMs, not by the client. Redirects and
    // compressed bodies pass through as they are, the handler adds no header (the tracing one, traceparent, included) to what
    // the adapter sent, and the HTTP client factory logs nothing, as its lines carry the request URL.
    private static void AddProviderClient(IServiceCollection services, string name, X509Certificate2? trusted) =>
        services.AddHttpClient(name, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                    UseCookies = false,
                    AutomaticDecompression = DecompressionMethods.None,
                    ActivityHeadersPropagator = null,
                };
                if (trusted is not null)
                {
                    handler.SslOptions.RemoteCertificateValidationCallback =
                        (_, certificate, _, errors) => CertificateTrust.IsTrusted(trusted, certificate, errors);
                }
                return handler;
            })
            .RemoveAllLoggers();

    private static IRequestHook? ReadHook(string providerId, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        if (!KnownHooks.TryGetValue(name, out var hook))
        {
            ExitWithError($"Providers:{providerId}:Hook is '{name}', which is not a known hook (known: {string.Join(", ", KnownHooks.Keys)})");
        }
        return hook;
    }

    private static X509Certificate2? ReadCertificate(string providerId, string? configuredPath, string contentRoot)
    {
        try
        {
            return CertificateTrust.Load(configuredPath, contentRoot);
        }
        catch (FileNotFoundException e)
        {
            ExitWithError($"Providers:{providerId}:TrustedCertificate points to '{e.FileName}', which does not exist. Run scripts/gen-cert.sh to create it.");
            throw new UnreachableException();
        }
    }

    [DoesNotReturn]
    private static void ExitWithError(string message)
    {
        Console.Error.WriteLine(message);
        Environment.Exit(1);
    }
}

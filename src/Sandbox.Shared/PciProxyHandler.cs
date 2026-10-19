using System.Text;

namespace Sandbox.Shared;

/// <summary>
/// Sends a request that carries a PCI marker (in its URL, a header value or its body, written raw or percent-encoded) to
/// the PCI Proxy instead of to the provider, and returns the proxy's response as it is, errors included. Any other
/// request goes on to the inner handler. A request with a marker never takes that route, so with the proxy down it fails.
/// The request that is re-sent carries <c>X-Proxy-TimeoutMs: 90000</c> unless it has an <c>X-Proxy-TimeoutMs</c> of its own.
/// </summary>
internal sealed class PciProxyHandler(IHttpClientFactory clients, Uri sendUri) : DelegatingHandler
{
    /// <summary>The named client the marker-bearing requests go out on. It never uses the Forwarder.</summary>
    internal const string ClientName = "pciproxy";

    private const string TimeoutHeader = "X-Proxy-TimeoutMs";

    // Below the 100 s default timeout of the adapters' HttpClients (the provider client and the "pciproxy" one), so that when a
    // provider never answers, the proxy's 504 PROVIDER_NO_RESPONSE arrives before the adapter's own timeout. Without this header
    // the proxy's default is 100 s too, and the adapter's timer, which started first, gives up first with the framework's 500.
    private const string DefaultProxyTimeoutMs = "90000";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!CarriesMarker(request, body))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // The same method, headers (content headers included) and body bytes; the proxy needs the original URL besides.
        using var forwarded = new HttpRequestMessage(request.Method, sendUri);
        foreach (var (name, values) in request.Headers)
        {
            forwarded.Headers.TryAddWithoutValidation(name, values);
        }
        if (body is not null)
        {
            forwarded.Content = new ByteArrayContent(body);
            foreach (var (name, values) in request.Content!.Headers)
            {
                forwarded.Content.Headers.TryAddWithoutValidation(name, values);
            }
        }
        forwarded.Headers.TryAddWithoutValidation("X-Proxy-Url", request.RequestUri!.AbsoluteUri);
        if (!forwarded.Headers.Contains(TimeoutHeader))
        {
            forwarded.Headers.TryAddWithoutValidation(TimeoutHeader, DefaultProxyTimeoutMs);
        }

        return await clients.CreateClient(ClientName).SendAsync(forwarded, cancellationToken);
    }

    // PciMarker.ContainsMarker is the proxy's own rule, so what it finds is what the proxy will replace or reject. The body is
    // read as Latin-1, one character per byte, which is what the proxy's scanner sees whatever the body's charset.
    private static bool CarriesMarker(HttpRequestMessage request, byte[]? body) =>
        PciMarker.ContainsMarker(request.RequestUri!.AbsoluteUri)
        || HeaderValues(request).Any(PciMarker.ContainsMarker)
        || (body is not null && PciMarker.ContainsMarker(Encoding.Latin1.GetString(body)));

    private static IEnumerable<string> HeaderValues(HttpRequestMessage request)
    {
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers = request.Headers;
        if (request.Content is not null)
        {
            headers = headers.Concat(request.Content.Headers);
        }
        return headers.SelectMany(header => header.Value);
    }
}

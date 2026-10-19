using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Primitives;
using Sandbox.Shared;

namespace PciProxy;

/// <summary>
/// The send endpoint: validates the request, replaces the markers in the URL, the headers and the body with the raw
/// values from the tokenizer, applies the provider's hook to the body, sends the result over the provider's client and
/// returns the provider's response. A request that cannot be completed is never sent: every failure is a
/// <see cref="ProxyException"/>.
/// </summary>
public sealed class SendPipeline(ProviderProfiles profiles, TokenizerClient tokenizer, ILoggerFactory loggerFactory)
{
    private const int DefaultTimeoutMs = 100_000;

    private static readonly HashSet<string> HopByHopHeaders =
        new(["Connection", "Keep-Alive", "TE", "Trailer", "Transfer-Encoding", "Upgrade"], StringComparer.OrdinalIgnoreCase);

    private readonly ILogger _logger = loggerFactory.CreateLogger("PciProxy");

    private sealed class Timings
    {
        public TimeSpan? Tokenizer { get; set; }
        public TimeSpan? Provider { get; set; }
    }

    /// <summary>Handles one request, logs one line for it, and throws the <see cref="ProxyException"/> to answer it with when it failed.</summary>
    public async Task HandleAsync(HttpContext context, string providerId)
    {
        var timings = new Timings();
        ProxyException? failure = null;
        string? unexpected = null;
        try
        {
            await ForwardAsync(context, providerId, timings);
        }
        catch (Exception e)
        {
            failure = e as ProxyException;
            if (failure is null)
            {
                // Only the type name is kept: the message of an exception can carry a URL or a value.
                unexpected = e.GetType().Name;
                failure = ProxyException.Error("The proxy could not handle the request.");
            }
        }

        LogRequest(context, providerId, failure?.Status ?? context.Response.StatusCode, failure?.Outcome ?? "Forwarded", failure?.Code, unexpected, timings);
        if (failure is not null)
        {
            throw failure;
        }
    }

    private async Task ForwardAsync(HttpContext context, string providerId, Timings timings)
    {
        var incoming = context.Request;
        var cancellationToken = context.RequestAborted;

        var url = ReadUrlHeader(incoming);
        var timeoutMs = ReadTimeoutHeader(incoming);
        var headers = incoming.Headers.Where(header => !IsNotForwarded(header.Key))
            .SelectMany(header => header.Value.Select(value => (Name: header.Key, Value: value!))).ToList();
        if (headers.Any(header => !Ascii.IsValid(header.Value)))
        {
            // HttpClient cannot write it, so it would fail with nothing sent: an invalid header, not a provider failure.
            throw ProxyException.MarkerSyntax("A header value has a character that is not ASCII.");
        }
        var body = await ReadBodyAsync(incoming, cancellationToken);

        // Everything that is sent, with its markers still in it. A bad marker or URL stops here, before the tokenizer is called.
        var tokens = new HashSet<string>(FindTokens(url));
        tokens.UnionWith(MarkerReplacer.FindTokens(body));
        foreach (var (_, value) in headers)
        {
            tokens.UnionWith(FindTokens(value));
        }
        ParseUrl(ReplaceMarkers(url, tokens.ToDictionary(token => token, _ => "0"), percentEncode: true));

        IReadOnlyDictionary<string, string> rawByToken = new Dictionary<string, string>();
        if (tokens.Count > 0)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                rawByToken = await tokenizer.DetokenizeAsync(tokens, cancellationToken);
            }
            finally
            {
                timings.Tokenizer = Stopwatch.GetElapsedTime(started);
            }
        }

        using var request = new HttpRequestMessage(new HttpMethod(incoming.Method), ParseUrl(ReplaceMarkers(url, rawByToken, percentEncode: true)));
        var contentHeaders = new List<(string Name, string Value)>();
        foreach (var (name, value) in headers)
        {
            var replaced = ReplaceMarkers(value, rawByToken, percentEncode: false);
            if (!request.Headers.TryAddWithoutValidation(name, replaced))
            {
                contentHeaders.Add((name, replaced)); // Content-Type and the like belong to the content
            }
        }
        var sentBody = MarkerReplacer.Replace(body, rawByToken, percentEncode: false);

        // The provider's profile applies to what is sent, so its hook sees the body with the raw values in it, never the markers.
        // A request without a body (a GET, say) has nothing to transform, and goes as it is.
        var profile = profiles.Get(providerId);
        if (profile.Hook is { } hook && sentBody.Length > 0)
        {
            var contentType = contentHeaders.Where(header => IsContentType(header.Name)).Select(header => header.Value).LastOrDefault();
            (sentBody, contentType) = hook.Apply(sentBody, contentType);
            contentHeaders.RemoveAll(header => IsContentType(header.Name));
            contentHeaders.Add(("Content-Type", contentType));
        }
        if (sentBody.Length > 0 || contentHeaders.Count > 0)
        {
            request.Content = new ByteArrayContent(sentBody);
            foreach (var (name, value) in contentHeaders)
            {
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await SendAsync(profile.Client, request, timeoutMs, timings, cancellationToken);
        var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken); // already buffered by SendAsync

        await WriteResponseAsync(context.Response, response, responseBody, timings, cancellationToken);
    }

    // The timeout covers the whole exchange with the provider, so the response is read completely (ResponseContentRead)
    // before anything is written back. Every failure of the exchange is a ProxyException.
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, int timeoutMs, Timings timings, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (Exception e) when ((e is OperationCanceledException or HttpRequestException) && !cancellationToken.IsCancellationRequested)
        {
            throw ProviderFailure(e, timedOut: timeout.IsCancellationRequested);
        }
        finally
        {
            timings.Provider = Stopwatch.GetElapsedTime(started);
        }
    }

    // What the provider can have seen decides the answer. Nothing, when no connection came to be (DNS, refused, TLS, proxy
    // tunnel): NotSent. Otherwise it is unknown, and so is a timeout, even one while connecting.
    private static ProxyException ProviderFailure(Exception e, bool timedOut) => e switch
    {
        _ when timedOut => ProxyException.ProviderNoResponse("The provider did not answer within X-Proxy-TimeoutMs."),
        HttpRequestException
        {
            HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError,
        } => ProxyException.ProviderUnreachable("The provider could not be reached."),
        _ => ProxyException.ProviderNoResponse("The provider did not complete its answer."),
    };

    private static async Task WriteResponseAsync(HttpResponse outgoing, HttpResponseMessage response, byte[] body, Timings timings, CancellationToken cancellationToken)
    {
        outgoing.StatusCode = (int)response.StatusCode;
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (!IsHopByHop(name))
            {
                outgoing.Headers.Append(name, values.ToArray());
            }
        }
        outgoing.Headers["X-Proxy-Outcome"] = "Forwarded";
        outgoing.Headers.Append("Server-Timing", string.Join(", ",
            new[] { ServerTiming("tokenizer", timings.Tokenizer), ServerTiming("provider", timings.Provider) }.Where(entry => entry is not null)));

        if (body.Length > 0)
        {
            outgoing.ContentLength ??= body.Length;
            await outgoing.Body.WriteAsync(body, cancellationToken);
        }
    }

    // A step that did not run has no Server-Timing entry.
    private static string? ServerTiming(string name, TimeSpan? duration) => duration is { } elapsed ? $"{name};dur={Milliseconds(elapsed)}" : null;

    private static string Milliseconds(TimeSpan duration) => duration.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture);

    private static string ReadUrlHeader(HttpRequest request)
    {
        var values = request.Headers["X-Proxy-Url"];
        return values.Count == 1 && !string.IsNullOrEmpty(values[0])
            ? values[0]!
            : throw ProxyException.MarkerSyntax("X-Proxy-Url is required, once: the absolute http or https URL of the provider.");
    }

    private static int ReadTimeoutHeader(HttpRequest request)
    {
        var values = request.Headers["X-Proxy-TimeoutMs"];
        if (values.Count == 0)
        {
            return DefaultTimeoutMs;
        }
        return values.Count == 1 && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var timeoutMs) && timeoutMs > 0
            ? timeoutMs
            : throw ProxyException.MarkerSyntax("X-Proxy-TimeoutMs must be a positive integer.");
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, cancellationToken);
        return body.ToArray();
    }

    // The URL as it goes on the wire. As a last line of defense, one round of percent-decoding must show no marker, whatever
    // the markers' own forms: a half-encoded or double-encoded one that was not replaced is not sent.
    private static Uri ParseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw ProxyException.MarkerSyntax("X-Proxy-Url must be an absolute http or https URL.");
        }
        if (PciMarker.ContainsMarker(Uri.UnescapeDataString(uri.AbsoluteUri)))
        {
            throw ProxyException.MarkerSyntax("X-Proxy-Url holds a PCI marker in a form the proxy does not replace.");
        }
        return uri;
    }

    // The proxy's own headers and the hop-by-hop ones are not sent on. Host comes from the URL, and Content-Length is
    // recomputed from the body, which is not the incoming one once the markers are replaced.
    private static bool IsNotForwarded(string name) =>
        IsHopByHop(name) || name.StartsWith("X-Proxy-", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);

    private static bool IsContentType(string name) => name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase);

    private static bool IsHopByHop(string name) =>
        HopByHopHeaders.Contains(name) || name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase);

    // Text (the URL, a header value) is searched and replaced as its UTF-8 bytes, but only when it has a marker, so
    // that every other value goes on exactly as it came in.
    private static IReadOnlyCollection<string> FindTokens(string text) =>
        PciMarker.ContainsMarker(text) ? MarkerReplacer.FindTokens(Encoding.UTF8.GetBytes(text)) : [];

    private static string ReplaceMarkers(string text, IReadOnlyDictionary<string, string> rawByToken, bool percentEncode) =>
        PciMarker.ContainsMarker(text)
            ? Encoding.UTF8.GetString(MarkerReplacer.Replace(Encoding.UTF8.GetBytes(text), rawByToken, percentEncode))
            : text;

    // One line per request, with the address it came from. It shows the host and the path of the original X-Proxy-Url with every marker as <marker>,
    // and never the query, a header, a body, a token or a raw value.
    private void LogRequest(HttpContext context, string providerId, int status, string outcome, string? code, string? unexpected, Timings timings)
    {
        var request = context.Request;
        var (host, path) = DescribeUrl(request.Headers["X-Proxy-Url"]);
        _logger.LogInformation(
            "send from={Origin} provider={ProviderId} method={Method} host={Host} path={Path} status={Status} outcome={Outcome} code={Code} error={Error} tokenizer_ms={Tokenizer} provider_ms={Provider}",
            context.GetOrigin(), SafeProviderId(providerId), request.Method, host, path, status, outcome, code ?? "-", unexpected ?? "-", LogDuration(timings.Tokenizer), LogDuration(timings.Provider));
    }

    private static string LogDuration(TimeSpan? duration) => duration is { } elapsed ? Milliseconds(elapsed) : "-";

    // The provider id comes from the caller: it is logged only when it looks like an id.
    private static string SafeProviderId(string providerId) =>
        providerId.Length is >= 1 and <= 64 && providerId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? providerId : "-";

    private static (string Host, string Path) DescribeUrl(StringValues header)
    {
        try
        {
            if (header.Count != 1 || string.IsNullOrEmpty(header[0]))
            {
                return ("-", "-");
            }
            var url = header[0]!;
            var masked = ReplaceMarkers(url, FindTokens(url).ToDictionary(token => token, _ => "<marker>"), percentEncode: false);
            if (PciMarker.ContainsMarker(Uri.UnescapeDataString(masked)))
            {
                return ("-", "-"); // a marker that was not replaced (double-encoded): nothing of the URL is shown
            }
            return Uri.TryCreate(masked, UriKind.Absolute, out var uri)
                ? (uri.Authority, uri.AbsolutePath.Replace("%3Cmarker%3E", "<marker>"))
                : ("-", "-");
        }
        catch (ProxyException)
        {
            return ("-", "-"); // a malformed marker: nothing of the URL is shown
        }
    }
}

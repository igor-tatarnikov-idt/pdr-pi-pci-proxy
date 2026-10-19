using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sandbox.Shared;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace PciProxy;

/// <summary>
/// The PDR's WillCall Gateway, as a YARP route of the PCI Proxy: <c>/will-call/{**path}</c> goes to the Will Call adapter
/// (the cluster in <c>ReverseProxy</c> in appsettings.json), and every 2xx JSON response has its PCI markers replaced with
/// the raw values from the tokenizer on the way back. Any other response streams through untouched.
/// </summary>
public static class WillCallGateway
{
    public const string RouteId = "will-call";

    private const string Prefix = "/" + RouteId;

    // The validators and digests of the upstream describe the bytes before the rewrite, not the ones the client gets.
    private static readonly string[] RepresentationHeaders = ["ETag", "Content-MD5", "Digest", "Content-Digest", "Repr-Digest"];

    /// <summary>Adds the response steps to the Will Call route; the request side (prefix, headers) is in the route's configuration.</summary>
    public static void AddTransforms(TransformBuilderContext context)
    {
        if (context.Route.RouteId != RouteId)
        {
            return;
        }
        var rewriter = context.Services.GetRequiredService<MarkerResponseRewriter>();
        context.AddResponseTransform(async response =>
        {
            if (response.ProxyResponse is not { } upstream)
            {
                return;
            }
            var outgoing = response.HttpContext.Response;
            // Hop-by-hop for the upstream connection: the headers that its Connection field lists (RFC 9110 section 7.6.1).
            foreach (var name in HttpRules.ConnectionTokens(upstream.Headers.Connection))
            {
                outgoing.Headers.Remove(name);
            }
            RewriteLocations(response, upstream);
            outgoing.Headers.Append("Via", HttpRules.Via);

            if (!upstream.IsSuccessStatusCode || !IsJson(upstream))
            {
                return;
            }

            // The raw account numbers are in this response: no cache may keep it, and no validator of the upstream stands for it.
            outgoing.Headers.CacheControl = "no-store";
            foreach (var name in RepresentationHeaders)
            {
                outgoing.Headers.Remove(name);
            }
            if (HttpMethods.IsHead(response.HttpContext.Request.Method))
            {
                return; // no body to rewrite; Content-Length stays that of the GET, as RFC 9110 section 9.3.2 wants
            }

            response.SuppressResponseBody = true;
            if (upstream.StatusCode == HttpStatusCode.PartialContent
                || upstream.Content.Headers.ContentEncoding.Any(coding => !coding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            {
                // A fragment or a compressed body cannot be rewritten, and passing it on would leave markers in it.
                await ProblemResponse.WriteAsync(response.HttpContext, new ProxyException(500, "NotSent", "PROXY_ERROR", "The response cannot be rewritten."));
                return;
            }
            var body = await upstream.Content.ReadAsByteArrayAsync(response.CancellationToken);
            byte[] rewritten;
            try
            {
                rewritten = await rewriter.RewriteAsync(body, response.CancellationToken);
            }
            catch (ProxyException e)
            {
                // Never an account number that is missing or a marker left in: the whole response is a 500, with the cause in X-Proxy-Code.
                await ProblemResponse.WriteAsync(response.HttpContext, new ProxyException(500, "NotSent", e.Code, e.Detail));
                return;
            }
            outgoing.ContentLength = rewritten.Length;
            await outgoing.Body.WriteAsync(rewritten, response.CancellationToken);
        });
    }

    /// <summary>
    /// The request steps before the proxying: TRACE is refused, the headers that the request's <c>Connection</c> field lists are
    /// dropped, and a request that has marker syntax anywhere (path, query, header values, body) is refused: see <see cref="MarkerRequestGuard"/>.
    /// </summary>
    public static async Task RejectMarkersAsync(HttpContext context, Func<Task> next)
    {
        if (HttpRules.IsTrace(context.Request.Method))
        {
            await HttpRules.WriteMethodNotAllowedAsync(context);
            return;
        }
        foreach (var name in HttpRules.ConnectionTokens(context.Request.Headers.Connection.Select(v => v!)))
        {
            context.Request.Headers.Remove(name);
        }
        if (await MarkerRequestGuard.CarriesMarkerAsync(context.Request))
        {
            await ProblemResponse.WriteAsync(context, ProxyException.MarkerSyntax("The request carries marker syntax."));
            return;
        }
        await next();
    }

    // The adapter knows nothing of the /will-call prefix, so a reference it sends (Swagger's redirect to /swagger/index.html, or
    // an absolute URL of its own) is made relative to the prefix. A reference to any other host stays as it is.
    private static void RewriteLocations(ResponseTransformContext response, HttpResponseMessage upstream)
    {
        var headers = response.HttpContext.Response.Headers;
        var adapter = upstream.RequestMessage?.RequestUri;
        foreach (var name in new[] { "Location", "Content-Location" })
        {
            if (headers[name].ToString() is { Length: > 0 } value && RewriteReference(value, adapter) is { } rewritten)
            {
                headers[name] = rewritten;
            }
        }
    }

    private static string? RewriteReference(string value, Uri? adapter)
    {
        if (value.StartsWith('/') && !value.StartsWith("//"))
        {
            return Prefix + value;
        }
        var absolute = value.StartsWith("//") && adapter is not null ? adapter.Scheme + ":" + value : value;
        return adapter is not null && Uri.TryCreate(absolute, UriKind.Absolute, out var uri)
            && uri.Scheme == adapter.Scheme && uri.Authority == adapter.Authority
            ? Prefix + uri.PathAndQuery + uri.Fragment
            : null;
    }

    private static bool IsJson(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType is { } type
        && (type.Equals("application/json", StringComparison.OrdinalIgnoreCase) || type.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Refuses what could make the gateway detokenize a token the payer chose: the Will Call adapter stores free text from payers
/// and returns it later, so a marker written into a request could come back in a response.
/// </summary>
internal static class MarkerRequestGuard
{
    public static async Task<bool> CarriesMarkerAsync(HttpRequest request)
    {
        if (PciMarker.ContainsMarker(request.Path + request.QueryString)
            || request.Headers.Values.Any(values => values.Any(value => value is not null && PciMarker.ContainsMarker(value))))
        {
            return true;
        }
        if (request.ContentLength is null or 0 && !request.Headers.ContainsKey("Transfer-Encoding"))
        {
            return false;
        }
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.Latin1, leaveOpen: true); // one character per byte, as the PCI Proxy's scanner reads it
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;
        return PciMarker.ContainsMarker(body);
    }
}

/// <summary>
/// Replaces the markers in the string values of a JSON document, whatever the field name. One tokenizer call per distinct token.
/// A document without a marker is returned byte for byte.
/// </summary>
public sealed class MarkerResponseRewriter(TokenizerClient tokenizer, ILoggerFactory loggerFactory)
{
    private static readonly JsonSerializerOptions Output = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ILogger _logger = loggerFactory.CreateLogger("PciProxy");

    public async Task<byte[]> RewriteAsync(byte[] body, CancellationToken cancellationToken)
    {
        if (!PciMarker.ContainsMarker(Encoding.Latin1.GetString(body)) && !PciMarker.ContainsMarker(Encoding.UTF8.GetString(body)))
        {
            return body;
        }

        JsonNode? document;
        try
        {
            document = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            throw ProxyException.Error("The response is not valid JSON.");
        }

        var strings = new List<(JsonValue Node, string Text)>();
        Collect(document, strings);
        var withMarkers = strings.Where(s => PciMarker.ContainsMarker(s.Text)).ToList();

        // A malformed marker throws MARKER_SYNTAX here, before any tokenizer call.
        var tokens = withMarkers.SelectMany(s => MarkerReplacer.FindTokens(Encoding.UTF8.GetBytes(s.Text))).Distinct().ToList();
        var raw = await tokenizer.DetokenizeAsync(tokens, cancellationToken);
        foreach (var (node, text) in withMarkers)
        {
            node.ReplaceWith(JsonValue.Create(Encoding.UTF8.GetString(MarkerReplacer.Replace(Encoding.UTF8.GetBytes(text), raw, percentEncode: false))));
        }

        // Counts only: never a token, a raw value or a body.
        _logger.LogInformation("will-call: replaced {Values} marker value(s), {Tokens} distinct token(s)", withMarkers.Count, tokens.Count);
        return JsonSerializer.SerializeToUtf8Bytes(document, Output);
    }

    private static void Collect(JsonNode? node, List<(JsonValue, string)> strings)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, child) in o.ToList())
                {
                    Collect(child, strings);
                }
                break;
            case JsonArray a:
                foreach (var child in a.ToList())
                {
                    Collect(child, strings);
                }
                break;
            case JsonValue v when v.TryGetValue<string>(out var text):
                strings.Add((v, text));
                break;
        }
    }
}

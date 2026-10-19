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

    /// <summary>Adds the response step to the Will Call route; the request side (prefix, headers) is in the route's configuration.</summary>
    public static void AddTransforms(TransformBuilderContext context)
    {
        if (context.Route.RouteId != RouteId)
        {
            return;
        }
        var rewriter = context.Services.GetRequiredService<MarkerResponseRewriter>();
        context.AddResponseTransform(async response =>
        {
            RewriteLocation(response);
            if (response.ProxyResponse is not { IsSuccessStatusCode: true } upstream || !IsJson(upstream))
            {
                return;
            }
            response.SuppressResponseBody = true;
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
            response.HttpContext.Response.ContentLength = rewritten.Length;
            await response.HttpContext.Response.Body.WriteAsync(rewritten, response.CancellationToken);
        });
    }

    /// <summary>
    /// One line per request, rejected ones included, with the address it came from: <c>will-call id=... from=... method=... path=... status=...</c>.
    /// The query is not logged.
    /// </summary>
    public static async Task LogRequestAsync(HttpContext context, Func<Task> next)
    {
        // The adapter gets the id too, so that its own lines can be matched with this one.
        if (!context.Request.Headers.ContainsKey(RequestId.Header) && RequestId.Current is { } id)
        {
            context.Request.Headers[RequestId.Header] = id;
        }
        try
        {
            await next();
        }
        finally
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("PciProxy").LogInformation(
                "will-call id={Id} from={Origin} method={Method} path={Path} status={Status}",
                RequestId.Current, context.GetOrigin(), context.Request.Method, context.Request.Path, context.Response.StatusCode);
        }
    }

    /// <summary>A request that has marker syntax anywhere (path, query, header values, body) is refused: see <see cref="MarkerRequestGuard"/>.</summary>
    public static async Task RejectMarkersAsync(HttpContext context, Func<Task> next)
    {
        if (await MarkerRequestGuard.CarriesMarkerAsync(context.Request))
        {
            await ProblemResponse.WriteAsync(context, ProxyException.MarkerSyntax("The request carries marker syntax."));
            return;
        }
        await next();
    }

    // The adapter knows nothing of the /will-call prefix, so a redirect it sends (Swagger's /swagger to /swagger/index.html) is made relative to the prefix.
    private static void RewriteLocation(ResponseTransformContext response)
    {
        var headers = response.HttpContext.Response.Headers;
        if (headers.Location.ToString() is ['/', ..] location && response.HttpContext.Request.Path.StartsWithSegments("/" + RouteId))
        {
            headers.Location = "/" + RouteId + location;
        }
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
        _logger.LogInformation("will-call-rewrite id={Id} values={Values} tokens={Tokens}", RequestId.Current, withMarkers.Count, tokens.Count);
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

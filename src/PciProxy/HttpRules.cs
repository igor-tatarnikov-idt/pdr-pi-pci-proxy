using Microsoft.Extensions.Primitives;

namespace PciProxy;

/// <summary>Rules of RFC 9110 that the send endpoint and the Will Call route share.</summary>
public static class HttpRules
{
    /// <summary>The <c>Via</c> entry (RFC 9110 section 7.6.3) of this proxy: protocol version and pseudonym, as the PDR's units keep no host names.</summary>
    public const string Via = "1.1 pci-proxy";

    /// <summary>Methods the proxy passes on. TRACE would echo the request, <c>Authorization</c> included, back to the caller.</summary>
    public const string AllowedMethods = "GET, HEAD, POST, PUT, PATCH, DELETE, OPTIONS";

    /// <summary>
    /// The header names that a <c>Connection</c> field lists (RFC 9110 section 7.6.1): they are hop-by-hop for that connection
    /// and must not be forwarded. <c>close</c> and <c>keep-alive</c> are listed too; they name no header that is forwarded anyway.
    /// </summary>
    public static HashSet<string> ConnectionTokens(IEnumerable<string> connectionValues) =>
        new(connectionValues.SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
            StringComparer.OrdinalIgnoreCase);

    public static bool IsTrace(string method) => HttpMethods.IsTrace(method);

    /// <summary>405 for TRACE, with the <c>Allow</c> field that a 405 must carry (RFC 9110 section 15.5.6).</summary>
    public static Task WriteMethodNotAllowedAsync(HttpContext context) =>
        ProblemResponse.WriteAsync(context,
            new ProxyException(405, "NotSent", "METHOD_NOT_ALLOWED", "The proxy does not pass this method on."),
            new Dictionary<string, StringValues> { ["Allow"] = AllowedMethods });
}

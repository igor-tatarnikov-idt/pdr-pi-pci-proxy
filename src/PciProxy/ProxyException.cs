namespace PciProxy;

/// <summary>
/// A failure the proxy answers with <c>application/problem+json</c>. <paramref name="outcome"/> says whether the provider
/// may have seen the request (<c>NotSent</c> or <c>Unknown</c>); <paramref name="code"/> is the <c>X-Proxy-Code</c>.
/// The detail goes to the caller and into nothing else: it never holds a token, a raw value or any part of the request.
/// </summary>
public sealed class ProxyException(int status, string outcome, string code, string detail) : Exception(detail)
{
    public int Status { get; } = status;
    public string Outcome { get; } = outcome;
    public string Code { get; } = code;
    public string Detail { get; } = detail;

    public static ProxyException MarkerSyntax(string detail) => new(400, "NotSent", "MARKER_SYNTAX", detail);

    public static ProxyException TokenInvalid(string detail) => new(422, "NotSent", "TOKEN_INVALID", detail);

    public static ProxyException TokenizerUnavailable(string detail) => new(503, "NotSent", "TOKENIZER_UNAVAILABLE", detail);

    public static ProxyException ProviderUnreachable(string detail) => new(502, "NotSent", "PROVIDER_UNREACHABLE", detail);

    public static ProxyException ProviderNoResponse(string detail) => new(504, "Unknown", "PROVIDER_NO_RESPONSE", detail);

    public static ProxyException Error(string detail) => new(500, "Unknown", "PROXY_ERROR", detail);
}

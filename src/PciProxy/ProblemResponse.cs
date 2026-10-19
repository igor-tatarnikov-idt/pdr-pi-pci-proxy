namespace PciProxy;

public static class ProblemResponse
{
    /// <summary>
    /// A proxy failure as problem details plus the headers that say whether the provider may have seen the request. When
    /// the response has already started it is too late to say anything, and the connection is aborted.
    /// </summary>
    public static async Task WriteAsync(HttpContext context, ProxyException e, IReadOnlyDictionary<string, Microsoft.Extensions.Primitives.StringValues>? extraHeaders = null)
    {
        var response = context.Response;
        if (response.HasStarted)
        {
            context.Abort();
            return;
        }
        response.Headers.Clear(); // whatever of the provider's response was already set
        response.ContentLength = null;
        response.Headers["X-Proxy-Outcome"] = e.Outcome;
        response.Headers["X-Proxy-Code"] = e.Code;
        foreach (var (name, values) in extraHeaders ?? new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>())
        {
            response.Headers[name] = values;
        }
        await Results.Problem(statusCode: e.Status, title: e.Code, detail: e.Detail).ExecuteAsync(context);
    }
}

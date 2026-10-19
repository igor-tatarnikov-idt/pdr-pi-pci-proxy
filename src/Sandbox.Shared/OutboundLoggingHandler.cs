using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Sandbox.Shared;

public static class OutboundLoggingExtensions
{
    /// <summary>Logs every outbound call as "out id=... method=... url=... body=..." and "res id=... status=... body=..." under the given logger category.</summary>
    public static IHttpClientBuilder AddOutboundLogging(this IHttpClientBuilder builder, string appName) =>
        builder.AddHttpMessageHandler(sp => new OutboundLoggingHandler(sp.GetRequiredService<ILoggerFactory>().CreateLogger(appName)));
}

internal sealed class OutboundLoggingHandler(ILogger logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (RequestId.Current is { } current && !request.Headers.Contains(RequestId.Header))
        {
            request.Headers.TryAddWithoutValidation(RequestId.Header, current);
        }
        var id = request.Headers.TryGetValues(RequestId.Header, out var ids) ? ids.First() : "-";
        var requestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        logger.LogInformation("out id={Id} method={Method} url={Url} body={Body}", id, request.Method, request.RequestUri, requestBody);

        var response = await base.SendAsync(request, cancellationToken);

        // Reading the content buffers it, so the caller can still read it afterwards.
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogInformation("res id={Id} status={Status} body={Body}", id, (int)response.StatusCode, responseBody);
        return response;
    }
}

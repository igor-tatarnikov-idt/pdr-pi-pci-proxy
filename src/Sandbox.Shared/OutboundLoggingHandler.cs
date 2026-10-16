using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Sandbox.Shared;

public static class OutboundLoggingExtensions
{
    /// <summary>Logs every outbound call as "--&gt; METHOD url body" and "&lt;-- status body" under the given logger category.</summary>
    public static IHttpClientBuilder AddOutboundLogging(this IHttpClientBuilder builder, string appName) =>
        builder.AddHttpMessageHandler(sp => new OutboundLoggingHandler(sp.GetRequiredService<ILoggerFactory>().CreateLogger(appName)));
}

internal sealed class OutboundLoggingHandler(ILogger logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        logger.LogInformation("--> {Method} {Uri} {Body}", request.Method, request.RequestUri, requestBody);

        var response = await base.SendAsync(request, cancellationToken);

        // Reading the content buffers it, so the caller can still read it afterwards.
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogInformation("<-- {StatusCode} {Body}", (int)response.StatusCode, responseBody);
        return response;
    }
}

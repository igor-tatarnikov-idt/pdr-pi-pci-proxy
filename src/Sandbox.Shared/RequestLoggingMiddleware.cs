using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Sandbox.Shared;

public static class RequestLoggingExtensions
{
    /// <summary>Logs every inbound request as "in id=... from=origin method=... target=/path?query body=..." under the given logger category.</summary>
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app, string appName) =>
        app.UseMiddleware<RequestLoggingMiddleware>(appName);
}

internal sealed class RequestLoggingMiddleware(RequestDelegate next, ILoggerFactory loggerFactory, string appName)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger(appName);

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var id = RequestId.FromRequestOrNew(request);
        RequestId.Current = id;
        context.Response.Headers[RequestId.Header] = id;
        request.EnableBuffering();

        string body;
        using (var reader = new StreamReader(request.Body, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync();
        }
        request.Body.Position = 0;

        _logger.LogInformation("in id={Id} from={Origin} method={Method} target={Target} body={Body}", id, context.GetOrigin(), request.Method, request.Path + request.QueryString, body);
        await next(context);
    }
}

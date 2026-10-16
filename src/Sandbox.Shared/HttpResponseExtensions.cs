using Microsoft.AspNetCore.Http;

namespace Sandbox.Shared;

public static class HttpResponseExtensions
{
    /// <summary>Passes a provider response through unchanged: status, body and content type.</summary>
    public static async Task<IResult> ToResultAsync(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        return Results.Content(body, contentType, statusCode: (int)response.StatusCode);
    }
}

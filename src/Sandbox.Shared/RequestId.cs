using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Sandbox.Shared;

/// <summary>
/// The request id that ties the log lines of one request together across the apps. The first app to see a request (the one
/// the curl call reaches) makes it up, and every call that app makes on the request's behalf carries it in <c>X-Request-Id</c>;
/// the next app logs it and passes it on. An id from the outside is kept only when it is 1 to 64 letters, digits, dots, dashes
/// or underscores, so that nothing can be written into a log line through it.
/// </summary>
public static partial class RequestId
{
    public const string Header = "X-Request-Id";

    private static readonly AsyncLocal<string?> CurrentId = new();

    /// <summary>The id of the request being handled; set by the inbound middleware and read by the outbound handlers.</summary>
    public static string? Current
    {
        get => CurrentId.Value;
        set => CurrentId.Value = value;
    }

    public static string New() => Guid.NewGuid().ToString("N")[..12];

    public static bool IsValid(string? value) => value is not null && Valid().IsMatch(value);

    /// <summary>The valid id the request came with, or a new one.</summary>
    public static string FromRequestOrNew(HttpRequest request) =>
        request.Headers[Header].Count == 1 && request.Headers[Header][0] is { } value && IsValid(value) ? value : New();

    /// <summary>Puts the id of the request being handled, if any, on every outbound request that has none.</summary>
    public static IHttpClientBuilder AddRequestId(this IHttpClientBuilder builder) =>
        builder.AddHttpMessageHandler(() => new RequestIdHandler());

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex Valid();
}

internal sealed class RequestIdHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (RequestId.Current is { } id && !request.Headers.Contains(RequestId.Header))
        {
            request.Headers.TryAddWithoutValidation(RequestId.Header, id);
        }
        return base.SendAsync(request, cancellationToken);
    }
}

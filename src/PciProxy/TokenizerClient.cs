using System.Net;
using System.Text.Json;
using Sandbox.Shared;

namespace PciProxy;

/// <summary>
/// Turns tokens into raw values with the tokenizer: one <c>POST /detokenize</c> per distinct token, in parallel. The
/// tokenizer rejecting a token (<c>400</c>, <c>404</c>, <c>422</c>) is <c>TOKEN_INVALID</c> at once. A tokenizer that
/// cannot be reached, times out, answers <c>5xx</c>, <c>408</c> or <c>429</c> is tried three times, 100 ms apart, and then
/// is <c>TOKENIZER_UNAVAILABLE</c>; so is any other answer that is not a success, at once, because trying again will not
/// change it.
/// </summary>
public sealed class TokenizerClient(IHttpClientFactory clients)
{
    private const int Attempts = 3;
    private static readonly TimeSpan PauseBetweenAttempts = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The raw value of every token. The first call to fail decides the answer and the others still in flight are
    /// cancelled, so an invalid token is not held up by a slow tokenizer call for another one.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> DetokenizeAsync(IEnumerable<string> tokens, CancellationToken cancellationToken)
    {
        var distinct = tokens.Distinct().ToArray();
        using var stopOthers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var calls = distinct.Select(token => DetokenizeOneAsync(token, stopOthers.Token)).ToList();
        var pending = calls.ToList();
        try
        {
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);
                await finished; // throws what the call failed with
            }
        }
        catch
        {
            await stopOthers.CancelAsync();
            await Task.WhenAny(Task.WhenAll(pending)); // let them end (cancelled) without their exceptions going unobserved
            throw;
        }
        return distinct.Zip(calls).ToDictionary(pair => pair.First, pair => pair.Second.Result);
    }

    private async Task<string> DetokenizeOneAsync(string token, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient("tokenizer");
        for (var attempt = 1; ; attempt++)
        {
            var raw = await TryDetokenizeAsync(client, token, cancellationToken);
            if (raw is not null)
            {
                // The value goes into URLs, headers and bodies as it is, so nothing outside this set gets through.
                return IsAllowedRawValue(raw) ? raw : throw ProxyException.TokenInvalid("The tokenizer returned a value the proxy does not accept.");
            }
            if (attempt == Attempts)
            {
                throw ProxyException.TokenizerUnavailable("The tokenizer is unavailable.");
            }
            await Task.Delay(PauseBetweenAttempts, cancellationToken);
        }
    }

    // The raw value; null when the tokenizer failed in a way worth another attempt (no connection, a timeout, 5xx, 408,
    // 429, an unreadable answer). Throws TOKEN_INVALID when the token is rejected, and TOKENIZER_UNAVAILABLE at once for a
    // status that another attempt will not change (401, 403, a redirect, ...).
    private static async Task<string?> TryDetokenizeAsync(HttpClient client, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.PostAsJsonAsync("detokenize", new DetokenizeRequest(token), cancellationToken);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
            {
                throw ProxyException.TokenInvalid("The tokenizer rejected a token of the request.");
            }
            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadFromJsonAsync<DetokenizeResponse>(cancellationToken))?.Value;
            }
            return IsWorthRetrying(response.StatusCode)
                ? null
                : throw ProxyException.TokenizerUnavailable("The tokenizer answered with a status the proxy does not accept.");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException
            || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested)) // the client's own timeout
        {
            return null;
        }
    }

    private static bool IsWorthRetrying(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    private static bool IsAllowedRawValue(string value) => value.Length is >= 1 and <= 64 && value.All(char.IsAsciiLetterOrDigit);
}

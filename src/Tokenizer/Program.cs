using System.Text.Json;
using Sandbox.Shared;

// The one token the sandbox tokenizer does not know; the PCI Proxy's TOKEN_INVALID path can be tested with it.
const string InvalidToken = "Y000000000000000";

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSandboxConsoleFormatter();
var app = builder.Build();

app.UseRequestLogging("Tokenizer");

// Same answer for every request (any body, or none); callers treat it as the detokenized bank account number.
// The only exception is InvalidToken, which is not found.
app.MapPost("/detokenize", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    return ReadToken(await reader.ReadToEndAsync()) == InvalidToken
        ? Results.Json(new { error = "token not found" }, statusCode: StatusCodes.Status404NotFound)
        : Results.Ok(new DetokenizeResponse("012345678901234567"));
});

app.Run();

// The token of a {"token":"..."} body; null for any other body.
static string? ReadToken(string body)
{
    try
    {
        return JsonSerializer.Deserialize<DetokenizeRequest>(body, JsonSerializerOptions.Web)?.Token;
    }
    catch (JsonException)
    {
        return null;
    }
}

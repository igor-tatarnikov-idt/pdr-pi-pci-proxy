using System.Text.Json;
using Sandbox.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSandboxConsoleFormatter();
var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EncryptedProvider");

app.UseRequestLogging("EncryptedProvider");

IResult Invalid(string reason, string? plaintext = null)
{
    log.LogWarning("rejected reason={Reason}{Plaintext}", reason, plaintext is null ? "" : ": " + plaintext);
    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
}

// Reads {"payload":"<base64>"} by hand (no parameter binding), decrypts it and parses the plaintext as T.
// Every failure maps to the same 400 and logs its own reason; on success the rejection is null.
async Task<(T? Value, IResult? Rejection)> ReadEncryptedAsync<T>(HttpRequest request, string what, Func<T, bool> isComplete)
    where T : class
{
    EncryptedEnvelope? envelope;
    try
    {
        envelope = await JsonSerializer.DeserializeAsync<EncryptedEnvelope>(request.Body, JsonSerializerOptions.Web);
    }
    catch (JsonException)
    {
        envelope = null;
    }
    if (string.IsNullOrEmpty(envelope?.Payload))
    {
        return (null, Invalid("payload missing or malformed"));
    }

    if (!PayloadCipher.TryDecrypt(envelope.Payload, out var plaintext))
    {
        return (null, Invalid("decryption failed"));
    }

    T? value;
    try
    {
        value = JsonSerializer.Deserialize<T>(plaintext, JsonSerializerOptions.Web);
    }
    catch (JsonException)
    {
        value = null;
    }
    if (value is null || !isComplete(value))
    {
        return (null, Invalid($"not a valid {what}", plaintext));
    }

    log.LogInformation("decrypted what={What} plaintext={Plaintext}", what, plaintext);
    return (value, null);
}

app.MapPost("/create-transaction", async Task<IResult> (HttpRequest request) =>
{
    var (_, rejection) = await ReadEncryptedAsync<Transaction>(request, "transaction", t =>
        t.TransactionId is not null && t.Currency is not null && t.SenderName is not null
        && t.RecipientName is not null && t.BankAccountNumber is not null);
    return rejection ?? Results.Json(new { status = "accepted" });
});

// The raw bank account number travels in the encrypted payload; every properly encrypted request is valid.
app.MapPost("/validate-transaction", async Task<IResult> (HttpRequest request) =>
{
    var (_, rejection) = await ReadEncryptedAsync<ValidateRequest>(request, "validation request", v => v.BankAccountNumber is not null);
    return rejection ?? Results.Json(true);
});

app.MapGet("/balance", () => new BalanceResponse(1000.00m, "USD"));

app.Run();

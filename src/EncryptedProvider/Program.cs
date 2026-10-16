using System.Text.Json;
using Sandbox.Shared;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EncryptedProvider");

app.UseRequestLogging("EncryptedProvider");

// The body is read by hand (no parameter binding) so every failure maps to the same 400 and logs its reason.
app.MapPost("/create-transaction", async Task<IResult> (HttpRequest request) =>
{
    IResult Invalid(string reason, string? plaintext = null)
    {
        log.LogWarning("rejected: {Reason}{Plaintext}", reason, plaintext is null ? "" : ": " + plaintext);
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
    }

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
        return Invalid("payload missing or malformed");
    }

    if (!PayloadCipher.TryDecrypt(envelope.Payload, out var plaintext))
    {
        return Invalid("decryption failed");
    }

    Transaction? transaction;
    try
    {
        transaction = JsonSerializer.Deserialize<Transaction>(plaintext, JsonSerializerOptions.Web);
    }
    catch (JsonException)
    {
        transaction = null;
    }
    if (transaction?.TransactionId is null || transaction.Currency is null || transaction.SenderName is null
        || transaction.RecipientName is null || transaction.BankAccountNumber is null)
    {
        return Invalid("not a valid transaction", plaintext);
    }

    log.LogInformation("decrypted transaction {Transaction}", plaintext);
    return Results.Json(new { status = "accepted" });
});

app.MapGet("/balance", () => new BalanceResponse(1000.00m, "USD"));

app.Run();

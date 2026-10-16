using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sandbox.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("tokenizer", c => c.BaseAddress = builder.Configuration.GetBaseUri("Tokenizer:BaseUrl"))
    .AddOutboundLogging("EncryptedAdapter");
builder.Services.AddHttpClient("provider", c => c.BaseAddress = builder.Configuration.GetBaseUri("Provider:BaseUrl"))
    .AddOutboundLogging("EncryptedAdapter");

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EncryptedAdapter");

// Base64 contains '+'; relaxed escaping keeps it as '+' so the envelope in the logs can be pasted into openssl.
var envelopeOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

app.MapPost("/create-transaction", async (IHttpClientFactory clients) =>
{
    // Detokenize the bank account the way adapters do today, then encrypt the whole payload for the provider.
    var tokenResponse = await clients.CreateClient("tokenizer")
        .PostAsJsonAsync("detokenize", new DetokenizeRequest("Y000000000000001"));
    var detokenized = await tokenResponse.Content.ReadFromJsonAsync<DetokenizeResponse>();

    var transaction = new Transaction("T-002-encrypted", 25.50m, "USD", "John Smith", "Jane Doe", detokenized!.Value);
    var json = JsonSerializer.Serialize(transaction, JsonSerializerOptions.Web);
    log.LogInformation("plaintext {Json}", json);

    var envelope = JsonSerializer.Serialize(new EncryptedEnvelope(PayloadCipher.Encrypt(json)), envelopeOptions);
    var response = await clients.CreateClient("provider")
        .PostAsync("create-transaction", new StringContent(envelope, Encoding.UTF8, "application/json"));
    return await response.ToResultAsync();
});

app.MapGet("/balance", async (IHttpClientFactory clients) =>
    await (await clients.CreateClient("provider").GetAsync("balance")).ToResultAsync());

app.Run();

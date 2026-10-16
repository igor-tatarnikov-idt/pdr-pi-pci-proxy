using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sandbox.Shared;

const string BankAccountToken = "Y000000000000001";

var builder = WebApplication.CreateBuilder(args);
var pciProxyMode = builder.Configuration.GetPciProxyMode(); // stops the app on an invalid PciProxy:Mode

builder.Services.AddHttpClient("tokenizer", c => c.BaseAddress = builder.Configuration.GetBaseUri("Tokenizer:BaseUrl"))
    .AddOutboundLogging("EncryptedAdapter");
builder.Services.AddHttpClient("provider", c => c.BaseAddress = builder.Configuration.GetBaseUri("Provider:BaseUrl"))
    .AddOutboundLogging("EncryptedAdapter")
    .ConfigureProviderHandler(builder.Configuration, builder.Environment.ContentRootPath);

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EncryptedAdapter");
log.LogPciProxyMode(pciProxyMode, builder.Configuration);

// Base64 contains '+'; relaxed escaping keeps it as '+' so the envelope in the logs can be pasted into openssl.
var envelopeOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

// Detokenize the bank account the way adapters do today; the raw value is what goes (encrypted) to the provider.
async Task<string> DetokenizeBankAccountAsync(IHttpClientFactory clients)
{
    var response = await clients.CreateClient("tokenizer")
        .PostAsJsonAsync("detokenize", new DetokenizeRequest(BankAccountToken));
    var detokenized = await response.Content.ReadFromJsonAsync<DetokenizeResponse>();
    return detokenized!.Value;
}

// Encrypts the whole payload and posts it to the provider as {"payload":"<base64>"}.
async Task<IResult> SendEncryptedAsync(IHttpClientFactory clients, string path, string plaintextJson)
{
    log.LogInformation("plaintext {Json}", plaintextJson);
    var envelope = JsonSerializer.Serialize(new EncryptedEnvelope(PayloadCipher.Encrypt(plaintextJson)), envelopeOptions);
    var response = await clients.CreateClient("provider")
        .PostAsync(path, new StringContent(envelope, Encoding.UTF8, "application/json"));
    return await response.ToResultAsync();
}

app.MapPost("/create-transaction", async (IHttpClientFactory clients) =>
{
    var transaction = new Transaction("T-002-encrypted", 25.50m, "USD", "John Smith", "Jane Doe", await DetokenizeBankAccountAsync(clients));
    return await SendEncryptedAsync(clients, "create-transaction", JsonSerializer.Serialize(transaction, JsonSerializerOptions.Web));
});

app.MapPost("/validate-transaction", async (IHttpClientFactory clients) =>
{
    var request = new ValidateRequest(await DetokenizeBankAccountAsync(clients));
    return await SendEncryptedAsync(clients, "validate-transaction", JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
});

app.MapGet("/balance", async (IHttpClientFactory clients) =>
    await (await clients.CreateClient("provider").GetAsync("balance")).ToResultAsync());

app.Run();

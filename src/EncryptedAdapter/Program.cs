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
    .ConfigureProviderHandler(builder.Configuration, builder.Environment.ContentRootPath)
    .AddPciProxy(builder.Configuration);

var app = builder.Build();
app.UseRequestLogging("EncryptedAdapter");
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

// The bank account for the provider's payload. In 'on' mode that is the marker, for the PCI Proxy to replace, and the
// tokenizer is not called; otherwise it is the raw value.
async Task<string> BankAccountAsync(IHttpClientFactory clients) =>
    pciProxyMode == PciProxyMode.On ? PciMarker.ForBankAccount(BankAccountToken) : await DetokenizeBankAccountAsync(clients);

// Posts the payload to the provider, encrypted as {"payload":"<base64>"}. In 'on' mode it goes as plaintext JSON instead:
// the PCI Proxy replaces the marker and encrypts it.
async Task<IResult> SendPayloadAsync(IHttpClientFactory clients, string path, string plaintextJson)
{
    var body = plaintextJson;
    if (pciProxyMode != PciProxyMode.On)
    {
        log.LogInformation("plaintext id={Id} json={Json}", RequestId.Current, plaintextJson);
        body = JsonSerializer.Serialize(new EncryptedEnvelope(PayloadCipher.Encrypt(plaintextJson)), envelopeOptions);
    }
    var response = await clients.CreateClient("provider")
        .PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
    return await response.ToResultAsync();
}

app.MapPost("/create-transaction", async (IHttpClientFactory clients) =>
{
    var transaction = new Transaction("T-002-encrypted", 25.50m, "USD", "John Smith", "Jane Doe", await BankAccountAsync(clients));
    return await SendPayloadAsync(clients, "create-transaction", JsonSerializer.Serialize(transaction, JsonSerializerOptions.Web));
});

app.MapPost("/validate-transaction", async (IHttpClientFactory clients) =>
{
    var request = new ValidateRequest(await BankAccountAsync(clients));
    return await SendPayloadAsync(clients, "validate-transaction", JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
});

app.MapGet("/balance", async (IHttpClientFactory clients) =>
    await (await clients.CreateClient("provider").GetAsync("balance")).ToResultAsync());

app.Run();

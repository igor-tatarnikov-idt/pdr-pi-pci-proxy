using System.Text;
using System.Text.Json;
using Sandbox.Shared;

const string BankAccountToken = "Y000000000000001";

var builder = WebApplication.CreateBuilder(args);
var pciProxyMode = builder.Configuration.GetPciProxyMode(); // stops the app on an invalid PciProxy:Mode

builder.Services.AddHttpClient("tokenizer", c => c.BaseAddress = builder.Configuration.GetBaseUri("Tokenizer:BaseUrl"))
    .AddOutboundLogging("PlainAdapter");
builder.Services.AddHttpClient("provider", c => c.BaseAddress = builder.Configuration.GetBaseUri("Provider:BaseUrl"))
    .AddOutboundLogging("PlainAdapter")
    .ConfigureProviderHandler(builder.Configuration, builder.Environment.ContentRootPath)
    .AddPciProxy(builder.Configuration);

var app = builder.Build();
app.Logger.LogPciProxyMode(pciProxyMode, builder.Configuration);

// Detokenize the bank account the way adapters do today; the raw value is what goes to the provider.
async Task<string> DetokenizeBankAccountAsync(IHttpClientFactory clients)
{
    var response = await clients.CreateClient("tokenizer")
        .PostAsJsonAsync("detokenize", new DetokenizeRequest(BankAccountToken));
    var detokenized = await response.Content.ReadFromJsonAsync<DetokenizeResponse>();
    return detokenized!.Value;
}

// The bank account as the provider's request carries it. In 'on' mode that is the marker, for the PCI Proxy to replace, and
// the tokenizer is not called; otherwise it is the raw value. A query string gets it percent-encoded.
async Task<string> BankAccountAsync(IHttpClientFactory clients, bool inQueryString = false)
{
    if (pciProxyMode == PciProxyMode.On)
    {
        return inQueryString ? PciMarker.ForUrl(BankAccountToken) : PciMarker.ForBankAccount(BankAccountToken);
    }
    var account = await DetokenizeBankAccountAsync(clients);
    return inQueryString ? Uri.EscapeDataString(account) : account;
}

app.MapPost("/create-transaction", async (IHttpClientFactory clients) =>
{
    var transaction = new Transaction("T-001-plain", 25.50m, "USD", "John Smith", "Jane Doe", await BankAccountAsync(clients));
    var json = JsonSerializer.Serialize(transaction, JsonSerializerOptions.Web);

    var response = await clients.CreateClient("provider")
        .PostAsync("create-transaction", new StringContent(json, Encoding.UTF8, "application/json"));
    return await response.ToResultAsync();
});

// This provider validates over GET and takes the raw bank account number in the query string.
app.MapPost("/validate-transaction", async (IHttpClientFactory clients) =>
{
    var account = await BankAccountAsync(clients, inQueryString: true);
    var response = await clients.CreateClient("provider")
        .GetAsync($"validate-transaction?bankAccountNumber={account}");
    return await response.ToResultAsync();
});

app.MapGet("/balance", async (IHttpClientFactory clients) =>
    await (await clients.CreateClient("provider").GetAsync("balance")).ToResultAsync());

app.Run();

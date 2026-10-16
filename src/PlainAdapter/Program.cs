using System.Text;
using System.Text.Json;
using Sandbox.Shared;

const string BankAccountToken = "Y000000000000001";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("tokenizer", c => c.BaseAddress = builder.Configuration.GetBaseUri("Tokenizer:BaseUrl"))
    .AddOutboundLogging("PlainAdapter");
builder.Services.AddHttpClient("provider", c => c.BaseAddress = builder.Configuration.GetBaseUri("Provider:BaseUrl"))
    .AddOutboundLogging("PlainAdapter");

var app = builder.Build();

// Detokenize the bank account the way adapters do today; the raw value is what goes to the provider.
async Task<string> DetokenizeBankAccountAsync(IHttpClientFactory clients)
{
    var response = await clients.CreateClient("tokenizer")
        .PostAsJsonAsync("detokenize", new DetokenizeRequest(BankAccountToken));
    var detokenized = await response.Content.ReadFromJsonAsync<DetokenizeResponse>();
    return detokenized!.Value;
}

app.MapPost("/create-transaction", async (IHttpClientFactory clients) =>
{
    var transaction = new Transaction("T-001-plain", 25.50m, "USD", "John Smith", "Jane Doe", await DetokenizeBankAccountAsync(clients));
    var json = JsonSerializer.Serialize(transaction, JsonSerializerOptions.Web);

    var response = await clients.CreateClient("provider")
        .PostAsync("create-transaction", new StringContent(json, Encoding.UTF8, "application/json"));
    return await response.ToResultAsync();
});

// This provider validates over GET and takes the raw bank account number in the query string.
app.MapPost("/validate-transaction", async (IHttpClientFactory clients) =>
{
    var account = await DetokenizeBankAccountAsync(clients);
    var response = await clients.CreateClient("provider")
        .GetAsync($"validate-transaction?bankAccountNumber={Uri.EscapeDataString(account)}");
    return await response.ToResultAsync();
});

app.MapGet("/balance", async (IHttpClientFactory clients) =>
    await (await clients.CreateClient("provider").GetAsync("balance")).ToResultAsync());

app.Run();

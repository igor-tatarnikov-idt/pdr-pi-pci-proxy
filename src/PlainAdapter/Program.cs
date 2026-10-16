using System.Text;
using System.Text.Json;
using Sandbox.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("tokenizer", c => c.BaseAddress = builder.Configuration.GetBaseUri("Tokenizer:BaseUrl"))
    .AddOutboundLogging("PlainAdapter");
builder.Services.AddHttpClient("provider", c => c.BaseAddress = builder.Configuration.GetBaseUri("Provider:BaseUrl"))
    .AddOutboundLogging("PlainAdapter");

var app = builder.Build();

app.MapPost("/create-transaction", async (IHttpClientFactory clients) =>
{
    // Detokenize the bank account the way adapters do today, then send the raw value to the provider.
    var tokenResponse = await clients.CreateClient("tokenizer")
        .PostAsJsonAsync("detokenize", new DetokenizeRequest("Y000000000000001"));
    var detokenized = await tokenResponse.Content.ReadFromJsonAsync<DetokenizeResponse>();

    var transaction = new Transaction("T-001-plain", 25.50m, "USD", "John Smith", "Jane Doe", detokenized!.Value);
    var json = JsonSerializer.Serialize(transaction, JsonSerializerOptions.Web);

    var response = await clients.CreateClient("provider")
        .PostAsync("create-transaction", new StringContent(json, Encoding.UTF8, "application/json"));
    return await response.ToResultAsync();
});

app.MapGet("/balance", async (IHttpClientFactory clients) =>
    await (await clients.CreateClient("provider").GetAsync("balance")).ToResultAsync());

app.Run();

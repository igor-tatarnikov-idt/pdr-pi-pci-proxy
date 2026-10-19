using Sandbox.Shared;

// What a database would hold: the tokens of the payers' bank accounts, never the raw numbers. They differ from the
// adapters' own token (Y000000000000001) so that a log line tells whose transaction it is.
var storedTransactions = new[]
{
    new Transaction("T-101-willcall", 100.00m, "USD", "Alice Sender", "Carlos Payee", "Y000000000000002"),
    new Transaction("T-102-willcall", 200.50m, "EUR", "Bob Sender", "Dana Payee", "Y000000000000003"),
};

var builder = WebApplication.CreateBuilder(args);
var pciProxyMode = builder.Configuration.GetPciProxyMode(); // stops the app on an invalid PciProxy:Mode

builder.Services.AddHttpClient("tokenizer", c => c.BaseAddress = builder.Configuration.GetBaseUri("Tokenizer:BaseUrl"))
    .AddOutboundLogging("WillCallAdapter");
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new() { Title = "Will Call Adapter", Version = "v1" }));

var app = builder.Build();
app.UseRequestLogging("WillCallAdapter");
app.Logger.LogPciProxyMode(pciProxyMode, builder.Configuration);

// Behind the PCI Proxy the app is reached under a path prefix that it never sees, which the proxy sends in
// X-Forwarded-Prefix. The Swagger document names it as the server, so "Try it out" calls the proxy, not the adapter's own port.
app.UseSwagger(options => options.PreSerializeFilters.Add((document, request) =>
{
    var prefix = request.Headers["X-Forwarded-Prefix"].ToString();
    if (prefix.Length > 0)
    {
        document.Servers = [new() { Url = prefix }];
    }
}));
app.UseSwaggerUI(options => options.SwaggerEndpoint("v1/swagger.json", "Will Call Adapter v1"));

// 'on': the account number is the marker, for the PCI Proxy to replace on its way to the client, and the tokenizer is not
// called. Otherwise the adapter detokenizes it itself, as adapters do today, and the client gets the raw value.
async Task<string> BankAccountAsync(IHttpClientFactory clients, string token)
{
    if (pciProxyMode == PciProxyMode.On)
    {
        return PciMarker.ForBankAccount(token);
    }
    var response = await clients.CreateClient("tokenizer").PostAsJsonAsync("detokenize", new DetokenizeRequest(token));
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<DetokenizeResponse>())!.Value;
}

app.MapGet("/lookup-transactions", async (IHttpClientFactory clients) =>
{
    var transactions = new List<Transaction>();
    foreach (var stored in storedTransactions)
    {
        transactions.Add(stored with { BankAccountNumber = await BankAccountAsync(clients, stored.BankAccountNumber) });
    }
    return transactions;
}).WithName("LookupTransactions");

app.Run();

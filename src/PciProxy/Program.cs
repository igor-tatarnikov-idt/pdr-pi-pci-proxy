using PciProxy;
using Sandbox.Shared;

var builder = WebApplication.CreateBuilder(args);

// The tokenizer client connects directly (never through the Forwarder or a system proxy), keeps no cookies (the proxy
// stores nothing between requests) and is not logged by the HTTP client factory, whose lines carry the request URL. It
// follows no redirect: it would send the token to wherever the Location says.
builder.Services.AddHttpClient("tokenizer", c =>
    {
        c.BaseAddress = builder.Configuration.GetBaseUri("Tokenizer:BaseUrl");
        c.Timeout = TimeSpan.FromSeconds(5); // one attempt
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
    .RemoveAllLoggers();

// The provider clients and hooks, by provider id, from Providers in appsettings.json.
ProviderProfiles.Add(builder.Services, builder.Configuration, builder.Environment.ContentRootPath);

builder.Services.AddSingleton<TokenizerClient>();
builder.Services.AddSingleton<SendPipeline>();

var app = builder.Build();

// Any method; the tenant is not used.
app.Map("/api/t/{tenantId}/providers/{providerId}/send", async (HttpContext context, string providerId, SendPipeline pipeline) =>
{
    try
    {
        await pipeline.HandleAsync(context, providerId);
    }
    catch (ProxyException e)
    {
        await WriteProblemAsync(context, e);
    }
});

app.Run();

// A proxy failure is problem details plus the headers that say whether the provider may have seen the request.
static async Task WriteProblemAsync(HttpContext context, ProxyException e)
{
    var response = context.Response;
    if (response.HasStarted)
    {
        context.Abort();
        return;
    }
    response.Headers.Clear(); // whatever of the provider's response was already set
    response.ContentLength = null;
    response.Headers["X-Proxy-Outcome"] = e.Outcome;
    response.Headers["X-Proxy-Code"] = e.Code;
    await Results.Problem(statusCode: e.Status, title: e.Code, detail: e.Detail).ExecuteAsync(context);
}

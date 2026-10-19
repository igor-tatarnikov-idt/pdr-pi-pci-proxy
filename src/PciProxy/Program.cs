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

// The Will Call route: YARP proxies /will-call/** to the Will Call adapter and WillCallGateway rewrites its JSON responses.
builder.Services.AddSingleton<MarkerResponseRewriter>();
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(WillCallGateway.AddTransforms);

var app = builder.Build();

// Any method; the tenant is not used.
app.Map("/api/t/{tenantId}/providers/{providerId}/send", async (HttpContext context, string providerId, SendPipeline pipeline) =>
{
    if (HttpRules.IsTrace(context.Request.Method))
    {
        await HttpRules.WriteMethodNotAllowedAsync(context);
        return;
    }
    try
    {
        await pipeline.HandleAsync(context, providerId);
    }
    catch (ProxyException e)
    {
        await ProblemResponse.WriteAsync(context, e);
    }
});

app.MapReverseProxy(pipeline => pipeline.Use(WillCallGateway.RejectMarkersAsync));

app.Run();

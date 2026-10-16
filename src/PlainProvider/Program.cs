using Sandbox.Shared;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseRequestLogging("PlainProvider");

// Accepts any body: the request log shows exactly what the adapter sent.
app.MapPost("/create-transaction", () => Results.Json(new { status = "accepted" }));

app.MapGet("/balance", () => new BalanceResponse(1000.00m, "USD"));

app.Run();

using Sandbox.Shared;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseRequestLogging("Tokenizer");

// Same answer for every request; callers treat it as the detokenized bank account number.
app.MapPost("/detokenize", () => new DetokenizeResponse("012345678901234567"));

app.Run();

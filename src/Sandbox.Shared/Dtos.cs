using System.Text.Json.Serialization;

namespace Sandbox.Shared;

public sealed record DetokenizeRequest(string Token);

public sealed record DetokenizeResponse(string Value);

public sealed record BalanceResponse(decimal Balance, string Currency);

public sealed record EncryptedEnvelope(string Payload);

/// <summary>The create-transaction payload. All six fields are required.</summary>
public sealed record Transaction(
    [property: JsonRequired] string TransactionId,
    [property: JsonRequired] decimal Amount,
    [property: JsonRequired] string Currency,
    [property: JsonRequired] string SenderName,
    [property: JsonRequired] string RecipientName,
    [property: JsonRequired] string BankAccountNumber);

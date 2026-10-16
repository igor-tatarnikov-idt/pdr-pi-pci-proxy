# PCI Proxy Sandbox — Design

Date: 2026-10-16
Status: approved by the PDR author (v2 plus the transaction-ID and `.sln` decisions below)
Source: PDR "Adapter PCI Proxy" (Confluence, Money Transfer space), Figure 1.

## Purpose

A local sandbox of the part of Figure 1 that exists today: **adapter → tokenizer** and
**adapter → provider**. The PCI Proxy, the Forwarder and the WillCall Gateway are not in it.

This is the PDR's `pci-proxy:off` behaviour: the adapter detokenizes the bank account on its own
and sends the raw account number to the provider. The sandbox lets the PDR author see that
exposure in the logs, and later put the PCI Proxy in front of a provider and compare.

The PDR author runs it on a laptop, calls the adapters with curl and watches each application's
console to see how a request travels. It may be attached to the PDR later. It is an
experimentation tool, not production code.

Success looks like this:

- One solution builds, and five ASP.NET Core applications start on fixed local ports.
- `curl` against an adapter produces readable log lines in the adapter, the tokenizer and the
  provider that show the whole hop chain, including the raw account number arriving at the provider.
- The encrypted provider answers `400` to a request that is not properly encrypted.
- Putting the PCI Proxy in front of a provider later takes a URL change in an adapter's
  configuration and no code change in the provider.

## Out of scope

- PCI Proxy, Forwarder, WillCall Gateway, the `pci-proxy:*` modes, PCI markers, `X-Proxy-*` headers.
- Authentication: every endpoint in every application is anonymous.
- Retries, timeouts, TLS, Docker.
- Real provider recipes (signing, mTLS, field-level encryption). One whole-payload cipher stands in
  for the encrypting providers. The cipher is deliberately the simplest possible and is not part
  of the exercise.
- Unit tests. Verification is a curl smoke script plus reading the logs.

## Topology

```
curl ─► PlainAdapter :5400 ──┬─► Tokenizer :5100         (bank account token → raw account number)
                             └─► PlainProvider :5200     (raw account number in a plain JSON body)

curl ─► EncryptedAdapter :5500 ──┬─► Tokenizer :5100     (bank account token → raw account number)
                                 └─► EncryptedProvider :5300  (encrypted body, 400 if not properly encrypted)
```

Only the adapters call the tokenizer. The providers call nothing. Nothing calls the tokenizer on
`GET /balance`.

## Solution layout

```
PciProxySandbox.sln            classic .sln (the .NET 10 default is .slnx; classic opens in
                               every IDE version)
Directory.Build.props          net10.0, Nullable, ImplicitUsings
README.md                      how to run, curl examples
scripts/run-all.sh             build once, start all five, prefix each log line with the app name
scripts/smoke.sh               curl each endpoint and check the HTTP status codes
src/
  Sandbox.Shared/              class library, no web host
  Tokenizer/                   :5100
  PlainProvider/               :5200
  EncryptedProvider/           :5300
  PlainAdapter/                :5400
  EncryptedAdapter/            :5500
```

All five applications are minimal-API web projects. Each binds with `"Urls"` in its
`appsettings.json`, for example `http://127.0.0.1:5100`, so `dotnet run` needs no launch profile.
JSON uses the ASP.NET Core defaults (camelCase).

### Sandbox.Shared

| Piece | Role |
|---|---|
| `PayloadCipher` | The simplest cipher that is still real encryption: AES-CBC (PKCS7) with a fixed dummy 32-byte key and a fixed 16-byte IV, output as base64. `Encrypt(string) → string` and `TryDecrypt(string, out string) → bool`; `TryDecrypt` returns `false` on bad base64 or a padding failure. It is unauthenticated and sandbox-only, and must never be reused. Key and IV are constants in this class, so adapter and provider cannot drift. |
| `RequestLoggingMiddleware` | Inbound logging. Logs `<-- METHOD /path?query body`. Buffers the request body so the endpoint can still read it. |
| `OutboundLoggingHandler` | `DelegatingHandler` for the adapters' `HttpClient`s. Logs `--> METHOD url body` and `<-- status body`. |
| DTOs | `Transaction(TransactionId, Amount, Currency, SenderName, RecipientName, BankAccountNumber)`, `DetokenizeRequest(Token)`, `DetokenizeResponse(Value)`, `BalanceResponse(Balance, Currency)`, `EncryptedEnvelope(Payload)`. |

Logging uses the standard console logger with the single-line simple formatter, configured in
each `appsettings.json`. Each application uses its own name as the logger category, so interleaved
output stays attributable.

## Applications

### Tokenizer — `:5100`

- `POST /detokenize` takes a tokenized value, `{"token":"..."}`, and returns the raw value. The sandbox
  accepts any body (or none), logs it through the request middleware, and always returns
  `200 {"value":"012345678901234567"}`.
- The constant is the same for every request. Callers treat it as the raw (detokenized) bank account
  number. The real IDT Pay Tokenizer has actions (for example `MTBAYD`); the sandbox ignores them.

### PlainProvider — `:5200`

- `POST /create-transaction` accepts any JSON body, logs it as received (the log shows exactly what
  the adapter sent, raw account number included), returns `200 {"status":"accepted"}`.
- `GET /balance` returns `200 {"balance":1000.00,"currency":"USD"}`.

### EncryptedProvider — `:5300`

- `POST /create-transaction` expects `{"payload":"<base64>"}`.
  - It decrypts the payload with `PayloadCipher`, then reads the plaintext as a `Transaction`.
  - The request is **invalid** if any of these happens: the body is not JSON or has no `payload`;
    `payload` is not valid base64; decryption fails; the plaintext is not JSON; any of the six
    `Transaction` fields is missing. The response is `400` problem-details with a short title, and
    the log line says which step failed.
  - On success the decrypted transaction is logged and the response is `200 {"status":"accepted"}`.
- `GET /balance` returns the same constant as the plain provider. It is not encrypted.

### PlainAdapter — `:5400`

- `POST /create-transaction`. It takes no request body: the payload is hard-coded in the handler.
  1. Call the tokenizer: `POST {Tokenizer:BaseUrl}/detokenize {"token":"Y000000000000001"}`. The token
     is a hard-coded placeholder. Read `value` from the response and treat it as the detokenized bank
     account number.
  2. Build the provider request from the hard-coded values and the detokenized account, and send it
     as JSON to the plain provider:
     ```json
     {
       "transactionId": "T-001-plain",
       "amount": 25.50,
       "currency": "USD",
       "senderName": "John Smith",
       "recipientName": "Jane Doe",
       "bankAccountNumber": "012345678901234567"
     }
     ```
  3. Return the provider's status and body to the caller unchanged.
- `GET /balance` calls `GET {Provider:BaseUrl}/balance` and returns the provider's status and body.
- Both outbound calls go through `OutboundLoggingHandler`.

### EncryptedAdapter — `:5500`

- `POST /create-transaction`, no request body, the same hard-coded payload and the same tokenizer
  call as the plain adapter, except that `transactionId` is `T-002-encrypted`.
  1. Call the tokenizer with the placeholder token and read `value` as the bank account number.
  2. Build the same `Transaction`, serialize it to JSON, encrypt it with `PayloadCipher`, and send
     `{"payload":"<base64>"}` to the encrypted provider.
  3. Return the provider's status and body unchanged.
- `GET /balance` calls the encrypted provider's `GET /balance` and returns its status and body.
- Both outbound calls go through `OutboundLoggingHandler`. The log shows the encrypted envelope as
  sent. The adapter also logs the plaintext it encrypted, so before and after are both visible.

The hard-coded values live inline in each adapter's create handler and are duplicated across the two
adapters. This keeps each adapter readable on its own, as in the original adapters.

## Configuration

| Application | Key | Default |
|---|---|---|
| all | `Urls` | `http://127.0.0.1:<port>` |
| both adapters | `Tokenizer:BaseUrl` | `http://127.0.0.1:5100` |
| PlainAdapter | `Provider:BaseUrl` | `http://127.0.0.1:5200` |
| EncryptedAdapter | `Provider:BaseUrl` | `http://127.0.0.1:5300` |

Every value can be overridden with an environment variable (`Provider__BaseUrl=...`), which is how a
PCI Proxy would be inserted later.

## Error handling

Only the encrypted provider has deliberate error handling: it rejects an improperly encrypted
request with `400`, as described above.

The adapters have no custom handling. They pass the provider's status and body through, so the
encrypted provider's `400` reaches curl as `400`. If the tokenizer or a provider is unreachable, the
framework's default `500` applies. There are no retries anywhere.

## Verification

`scripts/smoke.sh` runs against a started sandbox and checks status codes:

1. `POST` plain adapter `/create-transaction` → 200.
2. `GET` plain adapter `/balance` → 200.
3. `POST` encrypted adapter `/create-transaction` → 200.
4. `GET` encrypted adapter `/balance` → 200.
5. `POST` tokenizer `/detokenize` → 200.
6. `POST` encrypted provider `/create-transaction` with `{"payload":"not-valid"}` → 400.
7. `POST` encrypted provider `/create-transaction` with a plain, unencrypted transaction JSON → 400.

Whoever runs it then reads the console to confirm the log content: the plain provider logs the raw
account number, and the encrypted provider logs the decrypted transaction.

## Confirmed decisions

1. **Adapters call the tokenizer, not the providers.** Each adapter gets the raw account number from
   the tokenizer, then has everything it needs to call its provider. The providers call nothing.
2. **The PCI marker is out of the sandbox.** The first iteration shows the current state, where there
   are no markers. They are introduced later.
3. **Fixed transaction IDs that name the sending adapter.** Every call sends `T-001-plain` from the
   plain adapter and `T-002-encrypted` from the encrypted adapter.
4. **The cipher is AES-CBC with a fixed key and IV.** It is unauthenticated, so a wrong payload is
   caught by the padding check and the `Transaction` validation.
5. **Classic `.sln`** instead of the .NET 10 default `.slnx`. **.NET 10** as the target framework.

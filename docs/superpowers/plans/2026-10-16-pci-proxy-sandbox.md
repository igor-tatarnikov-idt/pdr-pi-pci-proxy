# PCI Proxy Sandbox Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build one .NET 10 solution with five anonymous ASP.NET Core apps (tokenizer, two providers, two adapters) that reproduce today's adapter → tokenizer → provider flow, observable through console logs and curl.

**Architecture:** Five minimal-API web projects plus one `Sandbox.Shared` class library (logging middleware, outbound logging handler, DTOs, a trivial AES cipher). Adapters call the tokenizer to detokenize a bank account token, then call their provider with a hard-coded payload. Verification is `scripts/smoke.sh` (curl status and body checks, extended task by task) plus reading logs.

**Tech Stack:** .NET 10 SDK (10.0.300 is installed; no `global.json`), ASP.NET Core minimal APIs, System.Text.Json, bash, curl, openssl.

**Spec:** `docs/superpowers/specs/2026-10-16-pci-proxy-sandbox-design.md`

## Global Constraints

- Target framework `net10.0`; `Nullable` and `ImplicitUsings` enabled, all set once in `Directory.Build.props`.
- Classic `.sln` file, not `.slnx`: `dotnet new sln --format sln`.
- Ports: Tokenizer 5100, PlainProvider 5200, EncryptedProvider 5300, PlainAdapter 5400, EncryptedAdapter 5500. Each app binds with `"Urls": "http://127.0.0.1:<port>"` in its `appsettings.json`.
- Every endpoint in every app is anonymous. JSON is camelCase (ASP.NET Core / `JsonSerializerDefaults.Web`).
- Tokenizer: `POST /detokenize` accepts any body, always returns `200 {"value":"012345678901234567"}`. The adapters' hard-coded bank account token is the placeholder `Y000000000000001`.
- Hard-coded payload (both adapters): `amount` 25.50, `currency` "USD", `senderName` "John Smith", `recipientName` "Jane Doe", `bankAccountNumber` = the tokenizer's `value`. `transactionId` is `T-001-plain` (PlainAdapter) and `T-002-encrypted` (EncryptedAdapter). The create endpoint takes no request body.
- Balance: both providers return `200 {"balance":1000.00,"currency":"USD"}`; both adapters pass it through and never call the tokenizer for `GET /balance`.
- Cipher: AES-CBC, PKCS7, base64 output, fixed dummy key (ASCII `sandbox-dummy-key-do-not-reuse!!`, 32 bytes) and fixed IV (ASCII `sandbox-iv-00000`, 16 bytes) as constants in `PayloadCipher`. Wire format `{"payload":"<base64>"}`.
- Only the encrypted provider has deliberate error handling: any improperly encrypted request gets `400` problem-details. Adapters have no custom error handling and pass the provider's status and body through. No retries.
- Logging: console logger, single-line simple formatter, configured in `appsettings.json`; logger category is the app's own name. Providers and tokenizer log inbound requests (`<-- METHOD /path?query body`); adapters log outbound requests (`--> METHOD url body`, `<-- status body`); adapters do not log inbound calls.
- Do not commit. The author commits the sandbox once they are satisfied; leave all changes uncommitted.

## Review Focus

1. Request logging must not consume the request body: the encrypted provider still reads it afterwards. Pinned by Task 3 "valid encrypted transaction → 200".
2. Malformed envelopes (empty body, non-JSON body, `{"payload":123}`, `{"payload":null}`, base64 that is not ciphertext) must return `400`, never `500`. Pinned by Task 3 checks.
3. A payload that decrypts cleanly but is not a full `Transaction` (non-JSON plaintext, missing field, `null` field) must return `400`. Pinned by Task 3 checks.
4. A provider's non-2xx status passes through an adapter (the encrypted provider's `400` reaches curl). Pinned by Task 4 step 6.
5. Stopping `run-all.sh` must not leave orphaned apps holding ports 5100–5500. Pinned by Task 6 step 5.

## File Structure

| Path | Responsibility |
|---|---|
| `PciProxySandbox.sln`, `Directory.Build.props` | Solution and shared build settings |
| `src/Sandbox.Shared/RequestLoggingMiddleware.cs` | Inbound logging middleware and `UseRequestLogging` |
| `src/Sandbox.Shared/OutboundLoggingHandler.cs` | Outbound logging `DelegatingHandler` and `AddOutboundLogging` |
| `src/Sandbox.Shared/HttpResponseExtensions.cs` | Turns a provider `HttpResponseMessage` into a pass-through `IResult` |
| `src/Sandbox.Shared/PayloadCipher.cs` | The trivial AES cipher |
| `src/Sandbox.Shared/Dtos.cs` | All shared records |
| `src/<App>/Program.cs`, `appsettings.json`, `<App>.csproj` | One minimal-API app each |
| `scripts/smoke.sh` | curl checks, grouped per app |
| `scripts/run-all.sh` | Build once, start all five, prefix logs |
| `README.md` | How to run and what to look for |

## Working conventions

- Start an app in the background from the repo root with `dotnet run --project src/<App> --no-launch-profile > <logfile> 2>&1 &`, wait until the log contains `Now listening on`, and stop it when the task's verification is done with `kill $(lsof -ti tcp:<port>)` (this kills the real listener; `dotnet run` can leave its child behind if only the wrapper is killed). Use `$TMPDIR` for log files.
- Every web project is created with `dotnet new web -o src/<App> --exclude-launch-settings` (a launch profile would override `Urls` from `appsettings.json`), then: delete `appsettings.Development.json`, delete the `TargetFramework`, `Nullable` and `ImplicitUsings` lines from the `.csproj` (`Directory.Build.props` supplies them), and add `dotnet sln add`.
- Each app's `appsettings.json` carries this logging block (written in Task 1, copied by later tasks). `System.Net.Http.HttpClient` at `Warning` silences the framework's own duplicate outbound lines:
  ```json
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning", "System.Net.Http.HttpClient": "Warning" },
    "Console": { "FormatterName": "simple", "FormatterOptions": { "SingleLine": true, "TimestampFormat": "HH:mm:ss " } }
  }
  ```

---

### Task 1: Solution skeleton, request logging, Tokenizer

**Files:**
- Create: `PciProxySandbox.sln`, `Directory.Build.props`
- Create: `src/Sandbox.Shared/Sandbox.Shared.csproj`, `RequestLoggingMiddleware.cs`, `Dtos.cs`
- Create: `src/Tokenizer/Tokenizer.csproj`, `Program.cs`, `appsettings.json`
- Create: `scripts/smoke.sh`
- Modify: `.gitignore` only if `bin/` and `obj/` are not already ignored

**Interfaces:**
- Produces, `Sandbox.Shared` namespace:
  - `static class RequestLoggingExtensions { static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app, string appName) }` logs `<-- {Method} {Path}{Query} {Body}` at Information to category `appName`, and leaves the body readable for the endpoint.
  - `record DetokenizeRequest(string Token)`, `record DetokenizeResponse(string Value)` in `Dtos.cs`.
  - `scripts/smoke.sh [group ...]` with groups `tokenizer plain-provider encrypted-provider plain-adapter encrypted-adapter` (no argument runs all), variables `TOKENIZER PLAIN_PROVIDER ENC_PROVIDER PLAIN_ADAPTER ENC_ADAPTER` (base URLs on the spec ports), `SAMPLE_TX='{"transactionId":"T-000","amount":1.00,"currency":"USD","senderName":"A","recipientName":"B","bankAccountNumber":"0123"}'`, and `check <name> <expected-status> <expected-body-substring-or-empty> <curl args...>` that prints `PASS <name>` or `FAIL <name> (got <status>, expected <status>)`. The script exits non-zero if any check failed.

- [ ] **Step 1: Scaffold.** `dotnet --version` prints 10.x. Create the `.sln` (classic), `Directory.Build.props`, `Sandbox.Shared` (`dotnet new classlib`, plus `<FrameworkReference Include="Microsoft.AspNetCore.App" />` so it can hold middleware), and `Tokenizer` (per Working conventions, referencing `Sandbox.Shared`). Delete the template `Class1.cs`. Run `dotnet build` → succeeds. Run `git status --short` → shows no `bin/` or `obj/` paths.
- [ ] **Step 2: Write the failing check.** Create `scripts/smoke.sh` per the interface, with the `tokenizer` group:
  ```bash
  check "tokenizer detokenizes to the constant" 200 '"value":"012345678901234567"' \
    -X POST "$TOKENIZER/detokenize" -H 'content-type: application/json' -d '{"token":"anything"}'
  ```
  Run `scripts/smoke.sh tokenizer` → `FAIL tokenizer detokenizes to the constant (got 000, expected 200)`, exit code 1.
- [ ] **Step 3: Implement `RequestLoggingMiddleware` and `UseRequestLogging` in `RequestLoggingMiddleware.cs`.** Enable request buffering, read the body, rewind the stream, then call the next delegate. Get the logger from `ILoggerFactory.CreateLogger(appName)`. Add the two DTOs to `Dtos.cs`.
- [ ] **Step 4: Implement `Tokenizer/Program.cs` and `appsettings.json`.** `appsettings.json` has the logging block and `"Urls": "http://127.0.0.1:5100"`. `POST /detokenize` returns `new DetokenizeResponse("012345678901234567")`; use `UseRequestLogging("Tokenizer")`; the endpoint must not bind or require a body.
- [ ] **Step 5: Run and verify.** Start Tokenizer (see Working conventions), run `scripts/smoke.sh tokenizer` → `PASS tokenizer detokenizes to the constant`. The Tokenizer log contains a line starting `<-- POST /detokenize` ending with `{"token":"anything"}`. Stop the app.

### Task 2: PlainProvider

**Files:**
- Create: `src/PlainProvider/PlainProvider.csproj`, `Program.cs`, `appsettings.json`
- Modify: `src/Sandbox.Shared/Dtos.cs`, `scripts/smoke.sh`

**Interfaces:**
- Consumes: `UseRequestLogging(app, appName)`.
- Produces: `record BalanceResponse(decimal Balance, string Currency)` in `Dtos.cs`.

- [ ] **Step 1: Write the failing checks** in a `plain-provider` group:
  ```bash
  check "plain provider accepts transaction" 200 '"status":"accepted"' \
    -X POST "$PLAIN_PROVIDER/create-transaction" -H 'content-type: application/json' -d "$SAMPLE_TX"
  check "plain provider balance" 200 '"balance":1000' "$PLAIN_PROVIDER/balance"
  ```
  Run `scripts/smoke.sh plain-provider` → both `FAIL (got 000 …)`.
- [ ] **Step 2: Implement.** Add `BalanceResponse`. `PlainProvider/Program.cs` with port 5200 and `UseRequestLogging("PlainProvider")`: `POST /create-transaction` returns `200 {"status":"accepted"}` for any body (no parsing, no validation); `GET /balance` returns `new BalanceResponse(1000.00m, "USD")`.
- [ ] **Step 3: Run and verify.** Start PlainProvider; `scripts/smoke.sh plain-provider` → both `PASS`. The log shows `<-- POST /create-transaction` followed by the full `$SAMPLE_TX` body, and `<-- GET /balance`. Stop the app.

### Task 3: EncryptedProvider and the cipher

**Files:**
- Create: `src/Sandbox.Shared/PayloadCipher.cs`
- Create: `src/EncryptedProvider/EncryptedProvider.csproj`, `Program.cs`, `appsettings.json`
- Modify: `src/Sandbox.Shared/Dtos.cs`, `scripts/smoke.sh`

**Interfaces:**
- Consumes: `UseRequestLogging`, `BalanceResponse`.
- Produces, `Sandbox.Shared`:
  - `static class PayloadCipher { static string Encrypt(string plaintext); static bool TryDecrypt(string payload, out string plaintext) }`. `TryDecrypt` returns `false` for non-base64 input and for any `CryptographicException`; it never throws for bad input.
  - `record EncryptedEnvelope(string Payload)`.
  - `record Transaction(string TransactionId, decimal Amount, string Currency, string SenderName, string RecipientName, string BankAccountNumber)`: all six are required, and a missing or `null` field makes it invalid.

- [ ] **Step 1: Write the failing checks** in an `encrypted-provider` group. Add to `smoke.sh` an independent encryptor built on openssl, which also cross-checks `PayloadCipher`:
  ```bash
  KEY_HEX=$(printf 'sandbox-dummy-key-do-not-reuse!!' | xxd -p -c 64)
  IV_HEX=$(printf 'sandbox-iv-00000' | xxd -p -c 64)
  encrypt()  { printf '%s' "$1" | openssl enc -aes-256-cbc -K "$KEY_HEX" -iv "$IV_HEX" -a -A; }
  envelope() { printf '{"payload":"%s"}' "$(encrypt "$1")"; }
  ```
  Then one `check` per row, all `POST "$ENC_PROVIDER/create-transaction" -H 'content-type: application/json'` with `-d <body>` (the empty-body row has no `-d`), and `check "encrypted provider balance" 200 '"balance":1000' "$ENC_PROVIDER/balance"`:

  | Check name | Body | Status |
  |---|---|---|
  | valid encrypted transaction | `envelope "$SAMPLE_TX"` | 200, body contains `"status":"accepted"` |
  | payload not base64 | `{"payload":"not-valid"}` | 400 |
  | payload base64 but not ciphertext | `{"payload":"AAAAAAAAAAAAAAAAAAAAAA=="}` | 400 |
  | plain unencrypted transaction | `$SAMPLE_TX` | 400 |
  | empty body | (none) | 400 |
  | body not JSON | `not json` | 400 |
  | payload is a number | `{"payload":123}` | 400 |
  | payload is null | `{"payload":null}` | 400 |
  | decrypts to non-JSON | `envelope 'hello'` | 400 |
  | decrypts to JSON missing senderName | `envelope` of `$SAMPLE_TX` without `senderName` | 400 |
  | decrypts to JSON with null recipientName | `envelope` of `$SAMPLE_TX` with `"recipientName":null` | 400 |

  Run `scripts/smoke.sh encrypted-provider` → every check `FAIL (got 000 …)`.
- [ ] **Step 2: Implement `PayloadCipher`** with `Aes.Create()` and the one-shot `EncryptCbc` / `DecryptCbc` (`PaddingMode.PKCS7`), UTF-8 plaintext, constants per Global Constraints. Add `EncryptedEnvelope` and `Transaction` to `Dtos.cs`.
- [ ] **Step 3: Implement `EncryptedProvider/Program.cs`** on port 5300 with `UseRequestLogging("EncryptedProvider")`. `POST /create-transaction` reads the body manually from `HttpRequest` (not minimal-API parameter binding), so that every failure maps to the same `400` and logs its own reason: envelope not parseable or `payload` not a string → `payload missing or malformed`; `TryDecrypt` false → `decryption failed`; plaintext not a valid `Transaction` → `not a valid transaction`. All three return `Results.Problem(statusCode: 400, title: "Invalid request")`. On success, log the decrypted transaction JSON and return `200 {"status":"accepted"}`. `GET /balance` returns `new BalanceResponse(1000.00m, "USD")`.
- [ ] **Step 4: Run and verify.** Start EncryptedProvider; `scripts/smoke.sh encrypted-provider` → all `PASS`. The log shows one reason line per rejected request (matching the three messages above) and one decrypted-transaction line for the valid request. Stop the app.

### Task 4: PlainAdapter

**Files:**
- Create: `src/Sandbox.Shared/OutboundLoggingHandler.cs`, `src/Sandbox.Shared/HttpResponseExtensions.cs`
- Create: `src/PlainAdapter/PlainAdapter.csproj`, `Program.cs`, `appsettings.json`
- Modify: `scripts/smoke.sh`

**Interfaces:**
- Consumes: `DetokenizeRequest`, `DetokenizeResponse`, `Transaction`.
- Produces, `Sandbox.Shared`:
  - `static class HttpClientBuilderExtensions { static IHttpClientBuilder AddOutboundLogging(this IHttpClientBuilder builder, string appName) }` wraps the client with `OutboundLoggingHandler`, which logs `--> {Method} {RequestUri} {Body}` and `<-- {StatusCode as int} {Body}` to category `appName` and leaves the response body readable.
  - `static class HttpResponseExtensions { static Task<IResult> ToResultAsync(this HttpResponseMessage response) }` returns the response's status, body and content type (default `application/json`) unchanged.

- [ ] **Step 1: Write the failing checks** in a `plain-adapter` group:
  ```bash
  check "plain adapter create" 200 '"status":"accepted"' -X POST "$PLAIN_ADAPTER/create-transaction"
  check "plain adapter balance" 200 '"balance":1000' "$PLAIN_ADAPTER/balance"
  ```
  Run `scripts/smoke.sh plain-adapter` → both `FAIL (got 000 …)`.
- [ ] **Step 2: Implement the two Shared helpers** per the interfaces above.
- [ ] **Step 3: Implement `PlainAdapter/Program.cs`** and `appsettings.json` (port 5400, `Tokenizer:BaseUrl` `http://127.0.0.1:5100`, `Provider:BaseUrl` `http://127.0.0.1:5200`). Two named `HttpClient`s, "tokenizer" and "provider", each `.AddOutboundLogging("PlainAdapter")`, base addresses from configuration. `POST /create-transaction` (no body): send `new DetokenizeRequest("Y000000000000001")` to `/detokenize`, read `DetokenizeResponse.Value` as the bank account number, build `new Transaction("T-001-plain", 25.50m, "USD", "John Smith", "Jane Doe", value)`, serialize with `JsonSerializerDefaults.Web`, `POST` it as `application/json` to the provider's `/create-transaction`, and return `response.ToResultAsync()`. `GET /balance` calls the provider's `/balance` and returns `ToResultAsync()`.
- [ ] **Step 4: Run and verify.** Start Tokenizer, PlainProvider and PlainAdapter. `scripts/smoke.sh plain-adapter` → both `PASS`. Then check the logs:
  - PlainAdapter log: `--> POST http://127.0.0.1:5100/detokenize {"token":"Y000000000000001"}`, `<-- 200 {"value":"012345678901234567"}`, and `--> POST http://127.0.0.1:5200/create-transaction` with a body containing `"transactionId":"T-001-plain"` and `"bankAccountNumber":"012345678901234567"`.
  - PlainProvider log: the same body, with the raw account number.
- [ ] **Step 5: Balance does not call the tokenizer.** Count `/detokenize` lines in the Tokenizer log, run `curl -s http://127.0.0.1:5400/balance`, count again → unchanged.
- [ ] **Step 6: Status pass-through (Review Focus 4).** Stop PlainAdapter, start EncryptedProvider, restart PlainAdapter with `Provider__BaseUrl=http://127.0.0.1:5300`, then `curl -s -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:5400/create-transaction` → `400`. Stop all apps.

### Task 5: EncryptedAdapter

**Files:**
- Create: `src/EncryptedAdapter/EncryptedAdapter.csproj`, `Program.cs`, `appsettings.json`
- Modify: `scripts/smoke.sh`

**Interfaces:**
- Consumes: `AddOutboundLogging`, `ToResultAsync`, `PayloadCipher.Encrypt`, `EncryptedEnvelope`, `Transaction`, `DetokenizeRequest`, `DetokenizeResponse`.

- [ ] **Step 1: Write the failing checks** in an `encrypted-adapter` group:
  ```bash
  check "encrypted adapter create" 200 '"status":"accepted"' -X POST "$ENC_ADAPTER/create-transaction"
  check "encrypted adapter balance" 200 '"balance":1000' "$ENC_ADAPTER/balance"
  ```
  Run `scripts/smoke.sh encrypted-adapter` → both `FAIL (got 000 …)`.
- [ ] **Step 2: Implement `EncryptedAdapter/Program.cs`** and `appsettings.json` (port 5500, `Provider:BaseUrl` `http://127.0.0.1:5300`, tokenizer as in Task 4, logger name `EncryptedAdapter`). Same tokenizer call and `Transaction` as Task 4 but `transactionId` `T-002-encrypted`. Serialize the `Transaction`, log it as `plaintext {json}`, send `new EncryptedEnvelope(PayloadCipher.Encrypt(json))` to the provider's `/create-transaction`, return `ToResultAsync()`. `GET /balance` passes the provider's response through.
- [ ] **Step 3: Run and verify.** Start Tokenizer, EncryptedProvider and EncryptedAdapter. `scripts/smoke.sh encrypted-adapter` → both `PASS`. Logs: EncryptedAdapter shows the tokenizer exchange, the `plaintext` line with `"transactionId":"T-002-encrypted"`, and the outbound `{"payload":"…"}` envelope; EncryptedProvider shows the decrypted transaction with the raw account number `012345678901234567`. Repeat Task 4 step 5 against port 5500 → no new `/detokenize` line. Stop all apps.

### Task 6: Run script, README, whole-sandbox verification

**Files:**
- Create: `scripts/run-all.sh`
- Modify: `README.md` (read it first; keep any existing title)

- [ ] **Step 1: Implement `scripts/run-all.sh`** (`#!/usr/bin/env bash`, executable). Resolve the repo root from the script's location, run `dotnet build PciProxySandbox.sln` once, then start each app in the background as a direct child, not through `dotnet run`: `( cd src/<App> && exec dotnet bin/Debug/net10.0/<App>.dll ) > >(awk '{ print "[<App>] " $0; fflush() }') 2>&1 &`. The `cd` makes the app's content root its project folder so `appsettings.json` is found; `exec` plus process substitution makes `$!` the app's own PID. Use `awk`, not `sed -u`, which BSD sed on macOS lacks. Record each `$!` in an array. Start order: Tokenizer, PlainProvider, EncryptedProvider, PlainAdapter, EncryptedAdapter. Install `trap 'trap - INT TERM EXIT; kill "${PIDS[@]}" 2>/dev/null' INT TERM EXIT` and then `wait`. Do not use `kill 0`: when the script is started in the background by another shell it shares that shell's process group, and `kill 0` would kill the caller too.
- [ ] **Step 2: Write `README.md`.** Cover: what the sandbox is (one paragraph, mirroring the spec's purpose); a port table; `scripts/run-all.sh` and `scripts/smoke.sh`; the four adapter curl commands (`curl -i -X POST http://127.0.0.1:5400/create-transaction`, `curl -i http://127.0.0.1:5400/balance`, and the same on 5500); what to look for in the logs (raw account number at the plain provider, decrypted transaction at the encrypted provider); how to repoint an adapter with `Provider__BaseUrl=...` (the hook for the PCI Proxy later); and a warning that the cipher key is a dummy.
- [ ] **Step 3: Run the whole sandbox.** Start `scripts/run-all.sh > $TMPDIR/run-all.log 2>&1 &`; poll `curl -s -o /dev/null http://127.0.0.1:5500/balance` until it answers. Run `scripts/smoke.sh` (no arguments) → every check `PASS`, exit code 0.
- [ ] **Step 4: Check the log prefixes.** `$TMPDIR/run-all.log` contains lines starting `[Tokenizer]`, `[PlainProvider]`, `[EncryptedProvider]`, `[PlainAdapter]` and `[EncryptedAdapter]`, and the full chain for one plain create (adapter → tokenizer → adapter → provider) is readable in order.
- [ ] **Step 5: No orphans (Review Focus 5).** Send `kill -TERM` to the `run-all.sh` process (a backgrounded non-interactive script ignores SIGINT; the author's own Ctrl-C test is in a real terminal). After three seconds `lsof -nP -iTCP:5100-5500 -sTCP:LISTEN` prints nothing.
- [ ] **Step 6: Final state.** `git status --short` lists only intended files (solution, props, `src/`, `scripts/`, `README.md`, `docs/`), none under `bin/`, `obj/` or log files. Nothing is committed.

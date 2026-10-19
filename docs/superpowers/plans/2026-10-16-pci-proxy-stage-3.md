# Stage 3: PCI Proxy and `pci-proxy:on` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the PDR's PCI Proxy (an ASP.NET Core app that detokenizes bank-account markers and calls the provider) and an `on` mode for both adapters, so that in `on` mode an adapter never calls the tokenizer and sends every request that carries a bank account through the proxy.

**Architecture:** A new `src/PciProxy` app exposes `/api/t/{tenantId}/providers/{providerId}/send` for any method. Its pipeline is validate → replace markers (URL, headers, body, as bytes) via one tokenizer call per distinct token → provider hook (`encrypt` for `encrypted-provider`) → send directly → return the provider's response with `X-Proxy-*` headers. Adapters get a `PciProxyHandler` that re-sends marker-bearing requests to the proxy; everything else keeps going through the Forwarder as in `egress`.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, `SocketsHttpHandler`, bash 3.2, curl, openssl, python3 (one-off captures), Podman/Envoy from stage 2.

**Spec:** `docs/superpowers/specs/2026-10-16-pci-proxy-sandbox-design.md`, section "Stage 3". The plans of stages 0 to 2 stay valid; this plan only adds to them.

## Global Constraints

- Work on branch `stage-3-pci-proxy`. Do not commit: the author commits when satisfied.
- Everything from stages 0 to 2 still holds (net10.0, classic `.sln`, anonymous endpoints, hard-coded payloads, curl smoke checks, bash 3.2 scripts, certificate trust by explicit path, Forwarder in Podman).
- **PciProxy app:** `src/PciProxy`, port `5700` (`"Urls": "http://127.0.0.1:5700"`), anonymous. One endpoint, any HTTP method: `/api/t/{tenantId}/providers/{providerId}/send`; `tenantId` is ignored. No authentication, no dry run (`X-Proxy-DryRun` is ignored), no digest, no response scrubbing, no response hook.
- **Request:** `X-Proxy-Url` (required, absolute http or https URL, may contain markers); `X-Proxy-TimeoutMs` (optional positive integer, default `100000`); body raw with the same `Content-Type`. Missing or invalid `X-Proxy-Url` or `X-Proxy-TimeoutMs` is `MARKER_SYNTAX`.
- **Markers:** `{{pci:ba:<token>}}`, token = `Y` plus 15 ASCII letters or digits. Found in the URL (also percent-encoded as `%7B%7Bpci%3Aba%3A<token>%7D%7D`, hex case-insensitive), in header values and in the body, on bytes. Raw value written percent-encoded in the URL, verbatim elsewhere. Anything that starts like a marker (`{{pci:` or its encoded form) but is not a well-formed marker (truncated, empty, wrong token shape) is `MARKER_SYNTAX`. A raw value that is not 1 to 64 ASCII letters or digits is `TOKEN_INVALID`.
- **Tokenizer client:** `Tokenizer:BaseUrl` = `http://127.0.0.1:5100`; `POST /detokenize {"token":"..."}`; one call per distinct token, in parallel. `400`, `404`, `422` answers are `TOKEN_INVALID` at once; a connection error, timeout or `5xx` is retried up to 2 more times with a 100 ms pause (3 attempts), then `TOKENIZER_UNAVAILABLE`. The sandbox tokenizer's invalid token is `Y000000000000000` (`404 {"error":"token not found"}`); every other token returns the constant as before.
- **Send:** the incoming method; the URL after replacement; incoming headers without `X-Proxy-*`, hop-by-hop headers (`Connection`, `Keep-Alive`, `Proxy-*`, `TE`, `Trailer`, `Transfer-Encoding`, `Upgrade`) and `Host` (host comes from the URL); body bytes; no redirects (`3xx` passes through), no retries, no automatic decompression, no proxy (direct, never the Forwarder); bounded by `X-Proxy-TimeoutMs`.
- **Response:** the provider's status, headers (minus hop-by-hop) and body, plus `X-Proxy-Outcome: Forwarded` and `Server-Timing: tokenizer;dur=<ms>, provider;dur=<ms>` (tokenizer entry only if something was detokenized). A provider's `4xx`/`5xx` is not a proxy error.
- **Errors** (`application/problem+json` with `X-Proxy-Outcome` on every proxy response and `X-Proxy-Code`): `400 NotSent MARKER_SYNTAX`; `422 NotSent TOKEN_INVALID`; `503 NotSent TOKENIZER_UNAVAILABLE`; `502 NotSent PROVIDER_UNREACHABLE` (DNS, refused, TLS before anything was written); `504 Unknown PROVIDER_NO_RESPONSE` (timeout or reset after the write; a timeout while connecting counts too); `500 Unknown PROXY_ERROR` (anything else). A request with an unresolved marker never reaches a provider.
- **Logging:** one line per request: provider id, method, host, path of the original `X-Proxy-Url` with every marker shown as `<marker>`, status, outcome, code, tokenizer and provider durations. Never a token, a raw value, a query string, a header or a body. Nothing stored or cached.
- **Profiles** (`Providers` in the proxy's `appsettings.json`, keyed by provider id): `plain-provider` has no entry (plain profile). `encrypted-provider`: `{"Hook":"encrypt","TrustedCertificate":"../../certs/sandbox.crt"}`. The `encrypt` hook runs after replacement: the body becomes `{"payload":"<PayloadCipher.Encrypt(body as UTF-8)>"}`, `Content-Type: application/json`, length recomputed. `TrustedCertificate` is applied like the adapters' (custom root, host name verified); a provider without an entry uses the system trust. An unknown `Hook` name stops the proxy at startup.
- **Adapters:** `PciProxy:Mode` is `off`, `egress` or `on` (any casing); anything else (including `shadow`) prints `PciProxy:Mode must be 'off', 'egress' or 'on' (got '<value>')` to stderr and exits 1. `on` adds `PciProxy:ProxyUrl` (`http://127.0.0.1:5700`), `PciProxy:TenantId` (`sandbox`), `PciProxy:ProviderId` (`plain-provider` for the PlainAdapter, `encrypted-provider` for the EncryptedAdapter); in `on` these and `PciProxy:ForwarderUrl` are validated at startup (absolute http/https URLs, non-empty ids) or the adapter exits 1.
- **`on` behavior:** the adapter never calls the tokenizer. It uses `{{pci:ba:Y000000000000001}}` where the raw account number was (create body; URL-encoded in the PlainAdapter's validate query). The EncryptedAdapter sends plaintext JSON containing the marker for create and validate; it does not encrypt. A request containing a marker in its URL, headers or body is re-sent to `<ProxyUrl>/api/t/<TenantId>/providers/<ProviderId>/send` with the same method, headers, body bytes and content type plus `X-Proxy-Url: <original absolute URL>`, on a client that never uses the Forwarder; the proxy's response (errors included) is returned unchanged. A request without a marker goes through the Forwarder as in `egress`. `off` and `egress` are unchanged.
- `scripts/run-all.sh` starts the PciProxy as `[PciProxy]` after the tokenizer; `PciProxy__Mode=on scripts/run-all.sh` runs the `on` flow.

## Findings relevant to the plan (verified in earlier stages)

- `HttpRequestException.HttpRequestError` (.NET 8+) distinguishes `ConnectionError`, `NameResolutionError`, `SecureConnectionError`, `ProxyTunnelError` from errors after the write; use it for `PROVIDER_UNREACHABLE` versus `PROVIDER_NO_RESPONSE`.
- `app.Map(pattern, handler)` matches every HTTP method.
- `*.localhost` names resolve to loopback on macOS, so the native PciProxy reaches `plain-provider.localhost:5200` and `encrypted-provider.localhost:5300` directly.
- The existing smoke `check` passes `--cacert "$CERT"` and does not expose response headers, so proxy checks need a new helper (Task 1).
- The author's own sandbox may be running on ports 5100–5601: stop nothing you did not start. If `lsof -nP -iTCP:5100-5700 -sTCP:LISTEN` is not empty before your first run, report NEEDS_CONTEXT.

## Review Focus

1. A request with an unresolved marker (invalid token, malformed marker) never reaches the provider. Pinned by Task 2 step 6.
2. The proxy never logs a token, a raw value, a query string, a header or a body. Pinned by Task 2 step 7.
3. The provider receives no `X-Proxy-*` or hop-by-hop header and the proxy's own `Host` is replaced; body bytes other than the marker are untouched, including non-UTF-8 bytes. Pinned by Task 1 step 6.
4. Markers are found in the URL (both forms), in a header value and in the body; one tokenizer call per distinct token (the same token twice is one call). Pinned by Task 1 steps 6 and 7.
5. In `on` the adapter never calls the tokenizer, and with the proxy down a marker-bearing call fails instead of going direct. Pinned by Task 4 steps 5 and 6.

## File Structure

| Path | Responsibility |
|---|---|
| `src/Sandbox.Shared/PciMarker.cs` | Marker format, URL form, token shape, marker detection |
| `src/Sandbox.Shared/CertificateTrust.cs` | Certificate loading and trust callback shared by adapters and the proxy (moved from `ProviderClientExtensions`) |
| `src/Sandbox.Shared/ProviderClientExtensions.cs` | `PciProxyMode` gains `On`; handler wiring for `on` |
| `src/Sandbox.Shared/PciProxyHandler.cs` | Adapter-side handler that re-sends marker-bearing requests to the proxy |
| `src/PciProxy/Program.cs`, `appsettings.json`, `PciProxy.csproj` | Host and configuration |
| `src/PciProxy/SendPipeline.cs` | The pipeline and response writing |
| `src/PciProxy/MarkerReplacer.cs`, `TokenizerClient.cs`, `ProxyException.cs` | Byte-level replacement, tokenizer client with retries, error type |
| `src/PciProxy/ProviderProfiles.cs` | `Providers` configuration, hooks, per-provider `HttpClient` (Task 3) |
| `src/Tokenizer/Program.cs` | The sandbox invalid token |
| `scripts/smoke.sh`, `scripts/run-all.sh`, `README.md`, spec | Checks, orchestration, docs |

## Working conventions

- Start the stack with `scripts/run-all.sh > $TMPDIR/<log> 2>&1 &` (add `PciProxy__Mode=on` for the `on` flow), wait until `curl -s -o /dev/null http://127.0.0.1:5500/balance` answers, stop it with `kill -TERM <pid>`, then check `lsof -nP -iTCP:5100-5700 -sTCP:LISTEN` is empty and `podman ps -a --filter name=sandbox-forwarder` lists nothing.
- A scratch "provider" for raw captures: a python server on `127.0.0.1:5892` that saves the raw bytes of one request (method line, headers, body) and answers `200`; reach it through the proxy with `X-Proxy-Url: http://plain-provider.localhost:5892/...` and provider id `plain-provider`.
- A second copy of an app for one-off checks: `Urls=http://127.0.0.1:<port> <Setting>__<Key>=<value> dotnet run --project src/<App> --no-launch-profile --no-build`.
- `dotnet build` must report 0 warnings and 0 errors after each code change.

---

### Task 1: PCI Proxy core: replacement and the plain profile

**Files:**
- Create: `src/Sandbox.Shared/PciMarker.cs`, `src/PciProxy/PciProxy.csproj`, `Program.cs`, `appsettings.json`, `SendPipeline.cs`, `MarkerReplacer.cs`, `TokenizerClient.cs`, `ProxyException.cs`
- Modify: `src/Tokenizer/Program.cs`, `PciProxySandbox.sln`, `scripts/smoke.sh`, `scripts/run-all.sh`

**Interfaces:**
- Produces, `Sandbox.Shared`: `static class PciMarker { static string ForBankAccount(string token); static string ForUrl(string token); static bool IsTokenShape(string token); static bool ContainsMarker(string text) }`. `ForBankAccount("Y000000000000001")` is `{{pci:ba:Y000000000000001}}`; `ForUrl` is `Uri.EscapeDataString` of it; `IsTokenShape` is `^Y[A-Za-z0-9]{15}$`; `ContainsMarker` is true for `{{pci:` or `%7B%7Bpci%3A` (hex case-insensitive).
- Produces, `PciProxy`: `ProxyException(int Status, string Outcome, string Code, string Detail)`; `MarkerReplacer.FindTokens(ReadOnlySpan<byte>) → IReadOnlyCollection<string>` (distinct tokens; throws `ProxyException` `MARKER_SYNTAX` on malformed markers) and `MarkerReplacer.Replace(ReadOnlySpan<byte>, IReadOnlyDictionary<string,string> rawByToken, bool percentEncode) → byte[]`; `TokenizerClient.DetokenizeAsync(IEnumerable<string> tokens, CancellationToken) → Task<IReadOnlyDictionary<string,string>>` (parallel, with the retry rules; throws `ProxyException`).
- Produces, `scripts/smoke.sh`: variables `PCI_PROXY=http://127.0.0.1:5700`, `SEND=$PCI_PROXY/api/t/sandbox/providers`; helper `check_proxy <name> <expected-status> <expected-outcome> <expected-code-or-empty> <body-substring-or-empty> <curl args...>` (runs curl with `-D <tmp> -o <tmp>`, compares the status, and matches `x-proxy-outcome: <outcome>` and, if given, `x-proxy-code: <code>` case-insensitively in the headers); group `pci-proxy`.

- [ ] **Step 1: Write the failing checks.** In `scripts/smoke.sh` add the variables, `check_proxy`, a tokenizer check `check "tokenizer: invalid token" 404 'token not found' -X POST "$TOKENIZER/detokenize" -H 'content-type: application/json' -d '{"token":"Y000000000000000"}'` in the `tokenizer` group, and these `pci-proxy` checks (`M='{{pci:ba:Y000000000000001}}'`, `ME='%7B%7Bpci%3Aba%3AY000000000000001%7D%7D'`, a transaction JSON `TX_M` with `"bankAccountNumber":"$M"`), all with `-H 'x-proxy-url: …'`:

  | Name | Request | Expect |
  |---|---|---|
  | plain create, marker in body | `POST $SEND/plain-provider/send`, url `http://plain-provider.localhost:5200/create-transaction`, `-d "$TX_M"` | 200, Forwarded, body `"status":"accepted"` |
  | plain validate, marker in query | `GET $SEND/plain-provider/send`, url `http://plain-provider.localhost:5200/validate-transaction?bankAccountNumber=$ME` | 200, Forwarded, body `true` |
  | provider 404 passes through | `GET`, url `http://plain-provider.localhost:5200/no-such-route` | 404, Forwarded |
  | method is preserved | `PUT`, url `http://plain-provider.localhost:5200/create-transaction`, `-d "$TX_M"` | 405, Forwarded |
  | missing X-Proxy-Url | `POST $SEND/plain-provider/send` | 400, NotSent, `MARKER_SYNTAX` |
  | bad X-Proxy-TimeoutMs | add `-H 'x-proxy-timeoutms: abc'` | 400, NotSent, `MARKER_SYNTAX` |

  Start the stack on the unchanged code (`scripts/run-all.sh`); `scripts/smoke.sh tokenizer pci-proxy` → the invalid-token check and every proxy check `FAIL` (`got 200` for the invalid token, `got 000` for the proxy). Stop the stack.
- [ ] **Step 2: Tokenizer.** In `src/Tokenizer/Program.cs` answer `404 {"error":"token not found"}` when the request's `token` is exactly `Y000000000000000`; any other request keeps returning the constant (the endpoint still accepts any body, or none).
- [ ] **Step 3: Scaffold the PciProxy app.** `dotnet new web -o src/PciProxy --exclude-launch-settings`; delete `appsettings.Development.json`; strip `TargetFramework`/`Nullable`/`ImplicitUsings` from the `.csproj` and reference `Sandbox.Shared`; `dotnet sln PciProxySandbox.sln add src/PciProxy`. `appsettings.json`: `Urls` `http://127.0.0.1:5700`, `Tokenizer:BaseUrl` `http://127.0.0.1:5100`, and the logging block used by the other apps. In `scripts/run-all.sh` add `PciProxy` to `APPS` right after `Tokenizer` and update the "five" in its header comment.
- [ ] **Step 4: Implement `PciMarker`, `MarkerReplacer`, `TokenizerClient`, `ProxyException`, `SendPipeline` and `Program.cs`** per Global Constraints, without the hook and profile logic (every provider is plain) and with the tokenizer retry rules, the error mapping and the log line implemented as far as this task's checks need; Task 2 completes the error rows. `Program.cs` maps `/api/t/{tenantId}/providers/{providerId}/send` with `app.Map`, writes problem-details with `X-Proxy-Outcome`/`X-Proxy-Code` for a `ProxyException`, and registers a "tokenizer" `HttpClient` and one provider `HttpClient` (`AllowAutoRedirect = false`, `UseProxy = false`, `AutomaticDecompression = None`). Replacement works on `byte[]`: the body as read, the URL and each header value as their ASCII/UTF-8 bytes.
- [ ] **Step 5: Run and verify.** `dotnet build` is clean. Start the stack; `scripts/smoke.sh tokenizer pci-proxy` → all `PASS`. Stop it.
- [ ] **Step 6: Raw capture (Review Focus 3).** Start the stack, run the scratch capture server on 5892, and call the proxy with provider id `plain-provider`, `X-Proxy-Url: http://plain-provider.localhost:5892/create-transaction?x=1`, a custom header `x-custom: <marker>`, and a body made of a marker between two non-UTF-8 bytes (for example `printf '\xff{{pci:ba:Y000000000000001}}\xfe'`). The saved request contains: the method and path with the query unchanged; `x-custom: 012345678901234567`; the body bytes `\xff012345678901234567\xfe`; `host: plain-provider.localhost:5892`; the original `content-type`; and no `x-proxy-*`, `connection`, `transfer-encoding` or marker anywhere. Repeat with the encoded form in the URL (`?a=%7B%7Bpci%3Aba%3AY000000000000001%7D%7D`): the saved request line has `?a=012345678901234567`.
- [ ] **Step 7: One call per distinct token (Review Focus 4).** Count `/detokenize` lines in the Tokenizer log, send a body holding the same marker twice → exactly 1 new line; send a body with two different valid tokens (`Y000000000000001` and `Y000000000000002`) → 2 new lines; both responses are `200` through the capture server. Stop everything.

### Task 2: PCI Proxy errors, retries, timeouts and logging

**Files:**
- Modify: `src/PciProxy/SendPipeline.cs`, `TokenizerClient.cs`, `ProxyException.cs`, `Program.cs` (as needed), `scripts/smoke.sh`

**Interfaces:**
- Consumes: Task 1's `ProxyException`, `check_proxy`, `SEND`.
- Produces: every row of the Global Constraints error table with the exact status, outcome and code, and the log line contract.

- [ ] **Step 1: Write the failing checks** in the `pci-proxy` group (use `check_proxy`):

  | Name | Request | Expect |
  |---|---|---|
  | empty marker | body `{{pci:ba:}}` to `plain-provider` create | 400, NotSent, `MARKER_SYNTAX` |
  | truncated marker | body `{{pci:ba:Y000` | 400, NotSent, `MARKER_SYNTAX` |
  | X-type token | body `{{pci:ba:X000000000000001}}` | 400, NotSent, `MARKER_SYNTAX` |
  | truncated encoded marker in the URL | url `…/validate-transaction?bankAccountNumber=%7B%7Bpci%3Aba%3A` | 400, NotSent, `MARKER_SYNTAX` |
  | invalid token | body with `{{pci:ba:Y000000000000000}}` | 422, NotSent, `TOKEN_INVALID` |
  | provider unreachable | url `http://127.0.0.1:5999/x` (nothing listens) | 502, NotSent, `PROVIDER_UNREACHABLE` |
  | unknown provider id is plain | `GET $SEND/some-other-id/send` with url `http://plain-provider.localhost:5200/balance` | 200, Forwarded, body `"balance":1000` |
  | proxy error body is problem-details | the invalid-token case | body contains `"status":422` (the `check_proxy` body-substring argument) |

  Run `scripts/smoke.sh pci-proxy` against Task 1's code → the new error checks `FAIL` for the reasons Task 1 left open.
- [ ] **Step 2: Complete the error mapping and retries** per Global Constraints: malformed markers and invalid headers are `MARKER_SYNTAX`; tokenizer `400/404/422` → `TOKEN_INVALID` immediately; connection error, timeout or `5xx` retried (3 attempts, 100 ms apart) then `TOKENIZER_UNAVAILABLE`; a raw value outside 1–64 ASCII letters or digits → `TOKEN_INVALID`; provider failures by `HttpRequestException.HttpRequestError` (`ConnectionError`, `NameResolutionError`, `SecureConnectionError`, `ProxyTunnelError` → `PROVIDER_UNREACHABLE`; a timeout or any error after the write → `PROVIDER_NO_RESPONSE`); anything else → `PROXY_ERROR`. Add the `Server-Timing` header and the log line per Global Constraints.
- [ ] **Step 3: Run and verify.** `scripts/smoke.sh tokenizer pci-proxy` → all `PASS`; keep the stack's proxy log for step 7. Stop only what you started.
- [ ] **Step 4: Tokenizer down.** With the stack running, stop the Tokenizer process only (`kill $(lsof -ti tcp:5100)`), call the proxy with a valid marker: `503`, `X-Proxy-Outcome: NotSent`, `X-Proxy-Code: TOKENIZER_UNAVAILABLE`, and the response takes about 200 ms or more (the retries). Restart it.
- [ ] **Step 5: Provider timeout.** Run a python server on 5893 that accepts and never answers; call the proxy with `X-Proxy-Url: http://plain-provider.localhost:5893/x` and `X-Proxy-TimeoutMs: 1000` → `504`, `Unknown`, `PROVIDER_NO_RESPONSE`, after about 1 s.
- [ ] **Step 6: Unresolved marker never reaches a provider (Review Focus 1).** With the capture server on 5892 running, send an invalid-token marker, a malformed marker and an empty marker, each with `X-Proxy-Url` pointing at it: every call returns its error status and the capture server has received no connection (its file is not written, or it is still waiting).
- [ ] **Step 7: The proxy log is clean (Review Focus 2).** After steps 3–6, the PciProxy log (`[PciProxy]` lines) contains one line per request with provider id, method, host, masked path, status, outcome, code and durations, and none of: `Y000000000000001`, `Y000000000000000`, `012345678901234567`, `bankAccountNumber`, `x-custom`, any query string, or a body. A request whose `X-Proxy-Url` carries a marker in its path (`…/accounts/{{pci:ba:Y000000000000001}}/x`) logs the path as `/accounts/<marker>/x`. Stop everything.

### Task 3: Provider profiles and the `encrypt` hook

**Files:**
- Create: `src/Sandbox.Shared/CertificateTrust.cs`, `src/PciProxy/ProviderProfiles.cs`
- Modify: `src/Sandbox.Shared/ProviderClientExtensions.cs`, `src/PciProxy/SendPipeline.cs`, `src/PciProxy/Program.cs`, `src/PciProxy/appsettings.json`, `scripts/smoke.sh`

**Interfaces:**
- Produces, `Sandbox.Shared`: `static class CertificateTrust { static X509Certificate2? Load(string? configuredPath, string contentRoot); static bool IsTrusted(X509Certificate2 trusted, X509Certificate? presented, SslPolicyErrors errors) }`: the existing private `LoadTrustedCertificate` and `IsTrusted` of `ProviderClientExtensions`, moved and made public with unchanged behavior; `ProviderClientExtensions` now calls them.
- Produces, `PciProxy`: `ProviderProfiles` reading `Providers` (`Hook`, `TrustedCertificate` per provider id); a lookup returning, per provider id, the `HttpClient` to use (one per entry that has a `TrustedCertificate`, with the validation callback; the shared default client for ids without an entry) and the hook (`encrypt` or none). The hook signature is `(byte[] Body, string ContentType) Apply(byte[] body, string? contentType)`.

- [ ] **Step 1: Write the failing checks** (`pci-proxy` group):

  | Name | Request | Expect |
  |---|---|---|
  | encrypted validate through the encrypted profile | `POST $SEND/encrypted-provider/send`, url `https://encrypted-provider.localhost:5300/validate-transaction`, plaintext body `{"bankAccountNumber":"$M"}` | 200, Forwarded, body `true` |
  | encrypted create through the encrypted profile | same provider id, url `…/create-transaction`, plaintext `$TX_M` | 200, Forwarded, body `"status":"accepted"` |
  | plain profile does not encrypt | provider id `plain-provider`, the same encrypted-provider URL and body | 502, NotSent, `PROVIDER_UNREACHABLE` (no pinned certificate for this id, so the TLS handshake fails before anything is written) |
  | encrypted provider rejects a bad payload through the proxy | provider id `encrypted-provider`, url `…/create-transaction`, body `not json` | 400, Forwarded (the provider's own 400 passes through) |

  Run against Task 2's code → the first two `FAIL` (`502 PROVIDER_UNREACHABLE`), the third already passes, the fourth `FAIL`.
- [ ] **Step 2: Move the certificate helpers** into `CertificateTrust` and make `ProviderClientExtensions` use them; build and run `scripts/smoke.sh plain-adapter encrypted-adapter encrypted-provider` on a started stack → still all `PASS` (no behavior change).
- [ ] **Step 3: Implement the profiles and the hook.** `ProviderProfiles` binds `Providers`, fails startup for an unknown `Hook` name (message naming the provider id and the hook) and for a `TrustedCertificate` that does not exist (the `gen-cert.sh` hint, as for the adapters). `SendPipeline` picks the client and hook by provider id after marker replacement; the `encrypt` hook uses `PayloadCipher.Encrypt` on the UTF-8 text of the body and serializes `new EncryptedEnvelope(...)` with relaxed JSON escaping, `Content-Type: application/json`. Add to `appsettings.json`: `"Providers": { "encrypted-provider": { "Hook": "encrypt", "TrustedCertificate": "../../certs/sandbox.crt" } }`.
- [ ] **Step 4: Run and verify.** Start the stack; `scripts/smoke.sh` (all groups, off mode) → all `PASS`. Stop it.
- [ ] **Step 5: Startup validation.** `Providers__bad__Hook=sign dotnet run --project src/PciProxy --no-launch-profile --no-build` exits non-zero with a message naming `bad` and `sign` and nothing listening on 5700; `Providers__encrypted-provider__TrustedCertificate=../../certs/missing.crt` exits non-zero mentioning `gen-cert.sh`.

### Task 4: Adapters in `on` mode

**Files:**
- Create: `src/Sandbox.Shared/PciProxyHandler.cs`
- Modify: `src/Sandbox.Shared/ProviderClientExtensions.cs`, `src/PlainAdapter/Program.cs`, `src/PlainAdapter/appsettings.json`, `src/EncryptedAdapter/Program.cs`, `src/EncryptedAdapter/appsettings.json`, `scripts/smoke.sh`

**Interfaces:**
- Consumes: `PciMarker` (Task 1), the proxy endpoint (Tasks 1–3), `AddOutboundLogging`.
- Produces, `Sandbox.Shared`: `PciProxyMode { Off, Egress, On }` and the updated `GetPciProxyMode` (message in Global Constraints); `PciProxyHandler(IHttpClientFactory clients, Uri sendUri) : DelegatingHandler`, which re-sends a marker-bearing request (marker in URL, header values or body, raw or encoded; detection by `PciMarker.ContainsMarker`) on the named client `"pciproxy"` and returns its response, and passes any other request to the inner handler; an extension `IHttpClientBuilder AddPciProxy(this IHttpClientBuilder builder, IConfiguration configuration)` that, in `on`, validates `PciProxy:ProxyUrl`, `TenantId`, `ProviderId` (exit 1 on failure), registers the `"pciproxy"` client (with `AddOutboundLogging` for the app name, never using a proxy) and adds the handler; and `LogPciProxyMode` printing `PciProxy mode=on proxy=<url> tenant=<t> provider=<p> forwarder=<url>`. `ConfigureProviderHandler` treats `On` like `Egress` for requests that reach the primary handler (the Forwarder), including the `ForwarderUrl` validation.
- Produces, `scripts/smoke.sh`: `PCI_PROXY_MODE=on` expectations in the `forwarder` group and an added encrypted-adapter balance call.

- [ ] **Step 1: Write the failing checks.** In the `forwarder` group add `curl -s -o /dev/null "$ENC_ADAPTER/balance"` to the adapter calls and an `on` branch (the `case` already has `egress` and `off`; keep the unknown-mode guard and mention `on` in its message): with the same `adapter_new`/`adapter_all`, `absent` for the plain create and validate patterns (they go to the proxy), `present` for the plain balance pattern, `present` for the `CONNECT encrypted-provider` pattern on `adapter_all`, `absent` for `detokenize|:5100|:5700` on `all`. Start the stack with `PciProxy__Mode=on scripts/run-all.sh` on the unchanged adapters (the setting is rejected there: the adapters exit 1 with the old message) → the adapter checks `FAIL`/`got 000`. Stop it.
- [ ] **Step 2: Implement the mode and the handler** per the interfaces. In both adapters add `.AddPciProxy(builder.Configuration)` to the provider client and add to each `appsettings.json` `"PciProxy"`: `ProxyUrl` `http://127.0.0.1:5700`, `TenantId` `sandbox`, `ProviderId` `plain-provider` (PlainAdapter) or `encrypted-provider` (EncryptedAdapter), next to `Mode` and `ForwarderUrl`.
- [ ] **Step 3: Switch the adapters' payloads in `on`.** In each adapter, a local function returns the bank account value: in `on` it is `PciMarker.ForBankAccount(BankAccountToken)` (and for the PlainAdapter's validate query `PciMarker.ForUrl(BankAccountToken)`), without calling the tokenizer; otherwise it detokenizes as today. The EncryptedAdapter, when the mode is `on`, sends the plaintext JSON (no `EncryptedEnvelope`, `Content-Type: application/json`) for both create and validate; in `off`/`egress` it encrypts as today.
- [ ] **Step 4: Run and verify all three modes.** `scripts/run-all.sh` then `scripts/smoke.sh` (off); `PciProxy__Mode=egress scripts/run-all.sh` then `PCI_PROXY_MODE=egress scripts/smoke.sh`; `PciProxy__Mode=on scripts/run-all.sh` then `PCI_PROXY_MODE=on scripts/smoke.sh` → every check `PASS` in each, with the Forwarder restarted between runs (`run-all.sh` does it). Stop after each run. In the `on` run the adapters' logs show `PciProxy mode=on …`, an outbound line `--> POST http://127.0.0.1:5700/api/t/sandbox/providers/<id>/send` carrying the marker, and no `/detokenize` call from the adapter.
- [ ] **Step 5: The adapter never calls the tokenizer (Review Focus 5).** With the `on` stack running, stop only the PciProxy process (`kill $(lsof -ti tcp:5700)`); count `/detokenize` lines in the Tokenizer log; `curl -s -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:5400/create-transaction` and the same for `:5500` and for `/validate-transaction` → not `200` each (connection refused to the proxy), the Tokenizer log gained no line and the PlainProvider/EncryptedProvider logs gained no line. Balance still returns `200` (it goes through the Forwarder). Restart the proxy afterwards with the stack's own command or stop the stack.
- [ ] **Step 6: No silent direct path and the raw capture.** With the stack in `on` and a capture server on 5892, run a second PlainAdapter on 5401 with `PciProxy__Mode=on Provider__BaseUrl=http://plain-provider.localhost:5892/`: `POST /create-transaction` shows, at the capture server, the raw account number `012345678901234567` in the body and no marker and no `x-proxy-*` header; the same for `POST /validate-transaction` (`bankAccountNumber=012345678901234567` in the saved request line, URL-encoded form resolved). Stop the extras.
- [ ] **Step 7: `shadow` and bad settings.** `PciProxy__Mode=shadow dotnet run --project src/PlainAdapter --no-launch-profile --no-build` (and the EncryptedAdapter) exits 1 with the message, nothing listening. In `on`, an empty `PciProxy__TenantId`, a non-URL `PciProxy__ProxyUrl` and an empty `PciProxy__ForwarderUrl` each exit 1 with a message naming the setting.

### Task 5: README, spec bookkeeping and final verification

**Files:**
- Modify: `README.md`, `docs/superpowers/specs/2026-10-16-pci-proxy-sandbox-design.md`

- [ ] **Step 1: README.** Add the PCI Proxy to the port table (5700) and the apps description; document the three modes and `PciProxy__Mode=on scripts/run-all.sh` with `PCI_PROXY_MODE=on scripts/smoke.sh`; document the send endpoint (headers, a curl example with a marker, the error codes), the profiles and what the `[PciProxy]` log shows and never shows; update "five apps" wording and the "Later" section if it is no longer accurate. Every command and claim must be true of the code: run the curl example as written.
- [ ] **Step 2: Spec.** Retitle Stage 3's "Points to confirm" to "Points confirmed" ("confirmed when the spec was approved") and add to "Confirmed decisions": 8. no response scrubbing and no dry run in the PCI Proxy; 9. `shadow` stays rejected. Make the spec's Stage 3 text match the implementation where it differs (note each difference in your report).
- [ ] **Step 3: Final verification in all three modes.** Start from nothing running: `scripts/run-all.sh` + `scripts/smoke.sh`; `PciProxy__Mode=egress` run + `PCI_PROXY_MODE=egress`; `PciProxy__Mode=on` run + `PCI_PROXY_MODE=on`: all `PASS`, no `FAIL`/`SKIP`. After each stop: no listeners on 5100–5700, no `sandbox-forwarder` container, a real-terminal Ctrl-C through a pty (`expect`) leaves the same state once.
- [ ] **Step 4: Final state.** `git status --short` lists only intended files (no `certs/`, `bin/`, `obj/`); nothing is committed; the branch is `stage-3-pci-proxy`.

# PCI Proxy Sandbox — Design

Date: 2026-10-16
Status: approved by the PDR author (v2 plus the transaction-ID and `.sln` decisions below)
Source: PDR "Adapter PCI Proxy" (Confluence, Money Transfer space), Figure 1.

## Purpose

A local sandbox of the part of Figure 1 that exists today: **adapter → tokenizer** and
**adapter → provider**. The WillCall Gateway is not in it. Stage 2 (below) adds the Forwarder and
stage 3 the PCI Proxy.

This is the PDR's `pci-proxy:off` behaviour: the adapter detokenizes the bank account on its own
and sends the raw account number to the provider. The sandbox lets the PDR author see that
exposure in the logs, and (from stage 3) put the PCI Proxy in front of a provider and compare.

The PDR author runs it on a laptop, calls the adapters with curl and watches each application's
console to see how a request travels. It may be attached to the PDR later. It is an
experimentation tool, not production code.

Success looks like this:

- One solution builds, and five ASP.NET Core applications start on fixed local ports (six from stage 3,
  with the PCI Proxy).
- `curl` against an adapter produces readable log lines in the adapter, the tokenizer and the
  provider that show the whole hop chain, including the raw account number arriving at the provider (in
  `on`, stage 3, the PCI Proxy is one more hop: it, not the adapter, calls the tokenizer and the provider,
  and it logs a line per request).
- The encrypted provider answers `400` to a request that is not properly encrypted.
- Putting the PCI Proxy in front of a provider takes a mode switch in an adapter's configuration
  (`PciProxy:Mode=on`, stage 3), with the provider's URL passed to the proxy in `X-Proxy-Url`, and no code
  change in the provider.

## Out of scope

- WillCall Gateway, and the `pci-proxy:shadow` mode with its dry run and digest. (The Forwarder and
  the `off` and `egress` modes arrive in stage 2; the PCI Proxy, PCI markers, `X-Proxy-*` headers and
  the `on` mode in stage 3.)
- Authentication: every endpoint in every application is anonymous.
- Retries and timeouts (except, in stage 3, the PCI Proxy's bounded tokenizer retries and its timeouts:
  `X-Proxy-TimeoutMs` and the tokenizer client's 5 s), and TLS other than the encrypted provider's
  self-signed certificate (stage 2).
  Docker and Podman are used for the Forwarder only.
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

This is the flow in `off` mode (`egress`, stage 2, sends the provider calls through the Forwarder; `on`,
stage 3, is described there). In `off` and `egress` only the adapters call the tokenizer; in `on` only the
PCI Proxy does. The providers call nothing. Nothing calls the tokenizer on `GET /balance`.

## Solution layout

```
PciProxySandbox.sln            classic .sln (the .NET 10 default is .slnx; classic opens in
                               every IDE version)
Directory.Build.props          net10.0, Nullable, ImplicitUsings
README.md                      how to run, curl examples
scripts/run-all.sh             build once, start every app (five here, six from stage 3), prefix each
                               log line with the app name
scripts/smoke.sh               curl each endpoint and check the HTTP status codes
src/
  Sandbox.Shared/              class library, no web host
  Tokenizer/                   :5100
  PlainProvider/               :5200
  EncryptedProvider/           :5300
  PlainAdapter/                :5400
  EncryptedAdapter/            :5500
```

Stage 2 adds `forwarder/` and `scripts/forwarder.sh`, and stage 3 a sixth application, `src/PciProxy/` on
`:5700`; `run-all.sh` then starts six apps.

All the applications are minimal-API web projects. Each binds with `"Urls"` in its
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
  accepts any body (or none), logs it through the request middleware, and returns
  `200 {"value":"012345678901234567"}` (but see the invalid token below).
- The constant is the same for every request, except the sandbox's invalid token `Y000000000000000`
  (stage 3), which answers `404 {"error":"token not found"}`. Callers treat the constant as the raw
  (detokenized) bank account number. The real IDT Pay Tokenizer has actions (for example `MTBAYD`); the sandbox ignores them.

### PlainProvider — `:5200`

- `POST /create-transaction` accepts any JSON body, logs it as received (the log shows exactly what
  the caller sent, raw account number included: the adapter in `off` and `egress`, the PCI Proxy in
  `on`), returns `200 {"status":"accepted"}`.
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

- `POST /create-transaction`. It takes no request body: the payload is hard-coded in the handler. Steps 1
  and 2 are the `off` and `egress` flow; in `on` (stage 3) the adapter skips step 1 and puts a marker
  where the raw account number is.
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
  call as the plain adapter, except that `transactionId` is `T-002-encrypted`. Steps 1 and 2 are the `off`
  and `egress` flow; in `on` (stage 3) the adapter does not call the tokenizer or encrypt, and sends
  plaintext JSON with a marker.
  1. Call the tokenizer with the placeholder token and read `value` as the bank account number.
  2. Build the same `Transaction`, serialize it to JSON, encrypt it with `PayloadCipher`, and send
     `{"payload":"<base64>"}` to the encrypted provider.
  3. Return the provider's status and body unchanged.
- `GET /balance` calls the encrypted provider's `GET /balance` and returns its status and body.
- Both outbound calls go through `OutboundLoggingHandler`. In `off` and `egress` the log shows the
  encrypted envelope as sent, and the adapter also logs the plaintext it encrypted, so before and after
  are both visible. In `on` (stage 3) the adapter encrypts nothing: the log shows the plaintext JSON with
  a marker that it sends to the PCI Proxy, and no plaintext line.

The hard-coded values live inline in each adapter's create handler and are duplicated across the two
adapters. This keeps each adapter readable on its own, as in the original adapters.

## Configuration

| Application | Key | Default |
|---|---|---|
| all but the EncryptedProvider | `Urls` | `http://127.0.0.1:<port>` |
| EncryptedProvider (stage 2) | `Kestrel:Endpoints:Https` (`Url` and the certificate's `Path` and `KeyPath`) | `https://127.0.0.1:5300`, `../../certs/sandbox.crt` and `../../certs/sandbox.key` |
| both adapters, PciProxy | `Tokenizer:BaseUrl` | `http://127.0.0.1:5100` |
| PlainAdapter | `Provider:BaseUrl` | `http://plain-provider.localhost:5200` |
| EncryptedAdapter | `Provider:BaseUrl` | `https://encrypted-provider.localhost:5300` |
| EncryptedAdapter (stage 2) | `Provider:TrustedCertificate` | `../../certs/sandbox.crt` |
| both adapters (stage 2) | `PciProxy:Mode` | `off` (`egress`, and from stage 3 `on`, are the other values) |
| both adapters (stage 2) | `PciProxy:ForwarderUrl` | `http://127.0.0.1:5600` (used in `egress` and `on`) |
| both adapters, `on` only (stage 3) | `PciProxy:ProxyUrl` | `http://127.0.0.1:5700` |
| both adapters, `on` only (stage 3) | `PciProxy:TenantId` | `sandbox` |
| both adapters, `on` only (stage 3) | `PciProxy:ProviderId` | `plain-provider` (PlainAdapter), `encrypted-provider` (EncryptedAdapter) |
| PciProxy (stage 3) | `Providers` | `{"encrypted-provider": {"Hook": "encrypt", "TrustedCertificate": "../../certs/sandbox.crt"}}`; `plain-provider` has no entry |

The paths are relative to the application's project folder. Every value can be overridden with an
environment variable (`Provider__BaseUrl=...`), which repoints an adapter. Stage 3 puts the PCI Proxy in
front differently, with `PciProxy__Mode=on`, because the proxy takes the provider's URL in `X-Proxy-Url`,
not as a base URL.

## Error handling

Only the encrypted provider has deliberate error handling (until stage 3, which adds the PCI Proxy's
error table): it rejects an improperly encrypted request with `400`, as described above.

The adapters have no custom handling. They pass the provider's status and body through, so the
encrypted provider's `400` reaches curl as `400`. What an unreachable tokenizer, provider, Forwarder or PCI
Proxy gives depends on the mode:

- **`off`**: the framework's default `500`, for the tokenizer and for the provider.
- **`egress`** (stage 2): the framework's `500` for the tokenizer, which is called directly, and for a
  Forwarder that cannot be reached. A provider that the Forwarder cannot reach is answered by the Forwarder
  with a `503`: the PlainAdapter passes it through, while the EncryptedAdapter's call is a `CONNECT` tunnel
  that the Forwarder refuses, so its `HttpClient` throws and the framework's `500` applies.
- **`on`** (stage 3): the adapter does not call the tokenizer. A request with a marker that the PCI Proxy
  cannot complete comes back as the proxy's error: its status, body and content type (`400`, `422`, `502`,
  `503`, `504` or `500`). The adapter adds `X-Proxy-TimeoutMs: 90000` to the request it re-sends, unless the
  request has such a header of its own. 90 s is below the 100 s default timeout of the adapter's own
  `HttpClient`, so a provider that never answers gives the proxy's `504 PROVIDER_NO_RESPONSE` after about
  90 s, not the framework's `500` after 100 s. (The proxy's 90 s are counted from its call to the provider,
  after its tokenizer step, so a tokenizer that needs retries can still let the adapter's 100 s run out
  first.) Only a PCI Proxy that cannot itself be reached gives the framework's `500` for a request with a
  marker. A request without a marker (balance) behaves as in `egress`: it goes through the Forwarder, with
  the same `503` (PlainAdapter) or `500` (EncryptedAdapter) when the Forwarder cannot reach the provider, and
  `500` when the Forwarder cannot be reached.

There are no retries in the adapters or the providers (the PCI Proxy of stage 3 retries its own tokenizer
calls).

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

## Stage 1: validate-transaction flow

Added next to the create flow. The tokenizer, the cipher and the hard-coded bank account token
(`Y000000000000001`) are unchanged. The flow exists to cover a real-life provider that takes the raw
bank account number in the **query string** of a `GET`.

| App | Endpoint | Behaviour |
|---|---|---|
| PlainProvider | `GET /validate-transaction?bankAccountNumber=<raw>` | The raw account number travels in the query string. Always returns `200 true`. The request log shows it in the URL. |
| EncryptedProvider | `POST /validate-transaction` | Expects `{"payload":"<base64>"}` whose plaintext is `{"bankAccountNumber":"<raw>"}`. Returns `200 true` for a properly encrypted request, and the same `400` problem-details as create otherwise, including a missing or `null` `bankAccountNumber`. |
| PlainAdapter | `POST /validate-transaction` | No request body. Detokenizes the token, then calls the plain provider with `GET validate-transaction?bankAccountNumber=<URL-escaped raw>` and passes the response through. |
| EncryptedAdapter | `POST /validate-transaction` | No request body. Detokenizes the token, encrypts `{"bankAccountNumber":"<raw>"}`, posts the envelope to the encrypted provider and passes the response through. |

The adapters' detokenizing in this table is the `off` and `egress` flow; in `on` (stage 3) they send a
marker for the PCI Proxy to replace.

`ValidateRequest(BankAccountNumber)` is the new shared DTO. The encrypted provider reads, decrypts and
parses both create and validate requests with one helper, so the two endpoints reject improper input
identically and log the same reasons.

`scripts/smoke.sh` gains: plain provider validate (query string) → 200 `true`; encrypted provider
validate with a valid encrypted request → 200 `true`, and with a payload that is not base64, an
unencrypted body, an empty body, and plaintext that is missing or has a `null` `bankAccountNumber` →
400 with the problem title; and both adapters' `POST /validate-transaction` → 200 `true`.

## Stage 2: Forwarder (Envoy), `PciProxy:Mode` and HTTPS

Adds the PDR's Forwarder lane. With `PciProxy:Mode=egress` an adapter sends its provider traffic to an
Envoy box that forwards it to the provider; with `off` it calls the provider directly, as before. The
sandbox now covers both plain HTTP (PlainProvider) and HTTPS (EncryptedProvider).

```
off:     adapter ──────────────────────────────► provider
egress:  adapter ─► Forwarder (Envoy, :5600) ───► provider      (tokenizer calls stay direct)
                    HTTP  : absolute-form request, forwarded as is
                    HTTPS : CONNECT tunnel, TLS stays between adapter and provider
```

### HTTPS for the EncryptedProvider

- It serves **HTTPS only** on `https://127.0.0.1:5300`, with a certificate loaded from the PEM files
  `certs/sandbox.crt` and `certs/sandbox.key` through Kestrel's `Endpoints` configuration (`Urls` is
  dropped for this app). The PlainProvider stays plain HTTP on `:5200`.
- `scripts/gen-cert.sh` creates a self-signed certificate with openssl (subject alternative names
  `encrypted-provider.localhost`, `localhost` and `127.0.0.1`) and does nothing if the files already
  exist. `certs/` is git-ignored and `run-all.sh` calls the script first. Nothing is installed into any
  system or keychain trust store.
- The adapters trust that certificate explicitly: `Provider:TrustedCertificate` (a path to
  `certs/sandbox.crt`) is added as a custom root for the provider client, and the host name is still
  verified. Any other certificate is rejected.
- `scripts/smoke.sh` calls the encrypted provider over HTTPS with `curl --cacert certs/sandbox.crt`.

### Names

- The adapters' provider URLs become `http://plain-provider.localhost:5200` and
  `https://encrypted-provider.localhost:5300`. macOS resolves every `*.localhost` name to loopback, so
  direct calls work unchanged. The Forwarder container cannot see the Mac's loopback, so it resolves
  the same names through `--add-host` entries that point at the Mac host. This mirrors the PDR's
  hostAliases, which the Forwarder must share with the adapters. The tokenizer stays at
  `http://127.0.0.1:5100`.

### Forwarder

- **Envoy 1.36.9**, the PDR's version. The image `docker.io/envoyproxy/envoy:v1.36.9` is already in the
  local Podman storage. Config: `forwarder/envoy.yaml`. It runs as the Podman container
  `sandbox-forwarder`, with the proxy listener published on `127.0.0.1:5600` and the admin interface
  on `127.0.0.1:5601` (container port 9901).
- `--add-host plain-provider.localhost:<H>` and `--add-host encrypted-provider.localhost:<H>`, where
  `<H>` is what `host.containers.internal` resolves to inside the container (192.168.127.254 on this
  machine). `host-gateway` does not work in this Podman setup, so `scripts/forwarder.sh` (which
  `run-all.sh` calls) looks `<H>` up with `getent` in the same image.
- One HTTP listener with Envoy's dynamic forward proxy: absolute-form requests (`allow_absolute_url`)
  for HTTP providers, and `CONNECT` tunnels for HTTPS providers. TLS is never terminated, so the
  adapter's TLS session ends at the provider. No logic, and no headers added to the provider request
  or response (request-id generation off, Envoy headers suppressed, no `X-Forwarded-*`). The access log
  goes to stdout.
- Access log details. The format ends with ` ua=%REQ(USER-AGENT)%`, so the adapters' requests (.NET's
  `HttpClient` sends no User-Agent, which shows as `ua=-`) can be told from curl's.
  `access_log_options.flush_log_on_tunnel_successfully_established` logs a `CONNECT` when the tunnel
  opens (and again, with the same start time, when it closes), so a tunnel shows in the log at once.
  The container runs Envoy with `--file-flush-interval-msec 100`, because Envoy otherwise buffers its
  stdout access log for about 10 s.
- `run-all.sh` starts it as `[Forwarder]`, removes a stale container first, and stops it on exit. If
  `podman` or its machine is unavailable it prints a warning and continues: `off` mode needs no
  Forwarder.

### `PciProxy:Mode`

- In each adapter's `appsettings.json`: `PciProxy:Mode` is `off` (the default) or `egress`, and
  `PciProxy:ForwarderUrl` is `http://127.0.0.1:5600`. The names follow the PDR's `pci-proxy:off|egress`.
  Any other value, including the later `shadow` and `on` (`on` is accepted from stage 3), stops the adapter
  at startup with a clear message. In `egress` an empty or non-absolute-http `PciProxy:ForwarderUrl` stops the adapter at
  startup too (otherwise an empty URL would silently go direct).
- `off` is unchanged. In `egress` the adapter's provider `HttpClient` uses the Forwarder as its proxy,
  so every provider call (create, validate and balance) leaves as an absolute-form request (HTTP) or a
  `CONNECT` tunnel (HTTPS). The tokenizer client never uses the proxy, because IDT's own services are
  not provider traffic. The adapter logs the mode and the Forwarder URL at startup, and its outbound
  log lines keep showing the logical provider URL.
- Switching mode: `PciProxy__Mode=egress scripts/run-all.sh`.
- One helper in `Sandbox.Shared` applies the mode and the certificate trust to the provider client, so
  the two adapters do not carry copies of the wiring.

### Verification

- Every existing adapter check in `scripts/smoke.sh` passes unchanged in both modes of this stage (`off`
  and `egress`; stage 3 adds `on`): the suite is run once per mode.
- A `forwarder` smoke group checks that Envoy's admin interface is ready and that the proxy forwards a
  plain-HTTP and a `CONNECT` request. It then reads the Forwarder's access log (`podman logs`): with
  `PCI_PROXY_MODE=egress` set for the run, the adapters' create, validate and balance calls to the plain
  provider and a `CONNECT` to the encrypted provider show in it, and without it they do not. The
  adapters' lines are the ones that end in `ua=-`, which tells them apart from the group's own curl
  requests (`ua=curl/...`). The checks look only at the lines after a `before` mark taken just before
  the adapter calls; the `CONNECT` is searched in the whole log, because a pooled tunnel may be reused.
- One-off checks, not part of the smoke script: a raw capture (a `nc` listener standing in for a
  provider) shows no headers added by Envoy; an invalid `PciProxy:Mode` makes the adapter exit non-zero
  with the message; an adapter rejects a provider certificate other than the sandbox one.

### Points confirmed

The PDR author confirmed these points when the spec was approved.

1. **`*.localhost` names with `--add-host` aliases** for the providers, instead of putting
   `host.containers.internal` in the URLs (the Mac cannot resolve that name, so direct mode would break).
2. **HTTPS only on the EncryptedProvider**, with a self-signed certificate that the adapters trust by
   explicit path, and nothing added to any keychain.
3. **Tokenizer calls stay direct** in both modes (and from stage 3 the PCI Proxy calls it directly too).
4. **`shadow` and `on` are rejected at startup** for now (`on` is accepted from stage 3, `shadow` stays rejected).
5. **A missing Podman only warns**: `run-all.sh` carries on and the sandbox works in `off` mode.

## Stage 3: PCI Proxy and `pci-proxy:on`

Adds the PDR's PCI Proxy lane. With `PciProxy:Mode=on` an adapter stops detokenizing: it puts a marker
`{{pci:ba:<token>}}` where the raw bank account number was and sends the request to the PCI Proxy,
which detokenizes it, post-processes the request for the provider, and calls the provider. Requests
without a marker, such as balance, still go through the Forwarder, as in `egress`.

```
on, request with a marker:
  adapter ─► PCI Proxy :5700 ─► Tokenizer :5100       (one detokenize call per distinct token)
                         └────► provider              (direct, raw value; encrypted for encrypted-provider)
on, request without a marker (balance):
  adapter ─► Forwarder :5600 ─► provider
```

### PCI Proxy — `:5700`

- A new ASP.NET Core app, `src/PciProxy`, anonymous for now. The PDR's machine-to-machine credential
  (`X-Proxy-Key`, Cognito) is out of scope.
- One endpoint, any HTTP method: `/api/t/{tenantId}/providers/{providerId}/send`. `tenantId` is read and
  ignored (the PDR has no tenant-specific behaviour).
- The request follows the PDR's "Send": the adapter sends the request it would have sent to the provider,
  with the same method, headers and raw body, markers in place of raw values, and the provider's full URL
  in `X-Proxy-Url` (required, given once; an absolute http or https URL that may contain markers).
  `X-Proxy-TimeoutMs` is optional, given at most once, a positive integer in digits only (at most
  2147483647, the largest 32-bit integer), and defaults to `100000`. There is no dry run: the proxy always sends the request, and
  ignores `X-Proxy-DryRun` if it is present.
- **Pipeline:** validate the request → find every marker in the URL, the headers and the body (a malformed
  marker or a bad URL stops the request here, before the tokenizer is called) → detokenize → replace the
  markers → find the provider entry by `providerId` (none means plain) and run its hook → send to the
  provider → return the provider's response with the proxy headers. There is no response hook and no
  response scrubbing.
- **Markers.** The marker is `{{pci:ba:<token>}}`; a token is `Y` plus 15 ASCII letters or digits. The
  proxy finds markers in the URL, in header values and in the body, working on bytes, and replaces each
  with the raw value: percent-encoded in the URL, unchanged in headers and bodies. Every character of a marker may be
  written literally or as a `%XX` escape (hex digits in either case), so the fully encoded form
  `%7B%7Bpci%3Aba%3A<token>%7D%7D` (what `Uri.EscapeDataString` writes) and the partly escaped form
  `%7B%7Bpci:ba:<token>%7D%7D` (what .NET puts on the wire for a URL written with a raw marker) are both
  markers. One level of decoding only: a double-encoded marker in the URL (`%257B%257Bpci...`) is not
  replaced but rejected as `MARKER_SYNTAX`, by a last-line guard that rejects the URL if, decoded once, it
  still starts like a marker after the replacement. In headers and bodies only the single-level forms are
  recognised; a double-encoded marker there is not a marker and goes on as it is. Anything that starts
  like a marker (`{{pci:`, so written or escaped) but is not well formed (truncated, empty, a token of the
  wrong shape) is `MARKER_SYNTAX`. A raw value that is not 1 to 64 ASCII letters or digits is
  `TOKEN_INVALID`.
- **Tokenizer.** One `POST {Tokenizer:BaseUrl}/detokenize {"token":"..."}` per distinct token, run in
  parallel (`Tokenizer:BaseUrl` is `http://127.0.0.1:5100`). The tokenizer client follows no redirect and
  bounds each attempt with a hard-coded 5 s timeout (not configurable, and outside `X-Proxy-TimeoutMs`).
  The answer decides what happens:
  - `400`, `404` and `422` are `TOKEN_INVALID` at once;
  - a connection error, an attempt that times out, `5xx`, `408`, `429`, and a `2xx` whose answer cannot
    be read, are retried up to 2 more times with a 100 ms pause (3 attempts), and then are
    `TOKENIZER_UNAVAILABLE`;
  - any other non-2xx answer (`401`, `403`, a `3xx`) is `TOKENIZER_UNAVAILABLE` at once, since trying again
    will not change it.

  When one of several distinct-token calls fails, the others are cancelled and that first failure is the
  answer.
- **Send.** The provider receives the incoming method, the URL after replacement, the incoming headers
  without `X-Proxy-*`, hop-by-hop headers (`Connection`, `Keep-Alive`, `Proxy-*`, `TE`, `Trailer`,
  `Transfer-Encoding`, `Upgrade`) and `Host` (the host comes from the URL), and the body bytes
  (`Content-Length` is recomputed, as the body changes with the replacement). It does not follow redirects
  (a `3xx` passes through), never retries, adds no header of its own (not even `traceparent`), keeps no
  cookies, does not decompress, and is bounded by `X-Proxy-TimeoutMs`, which covers the whole exchange with
  the provider: the proxy reads the provider's answer completely before it writes anything back. It
  connects directly, not through the Forwarder.
- **Response.** The provider's status, headers (minus the hop-by-hop ones) and body are returned as they
  are, plus `X-Proxy-Outcome: Forwarded` and `Server-Timing: tokenizer;dur=<ms>, provider;dur=<ms>` (the
  tokenizer entry only when something was detokenized). A provider's own `4xx` or `5xx` is not a proxy
  error.
- **Errors** are `application/problem+json` with `X-Proxy-Outcome` (present on every proxy response) and
  `X-Proxy-Code`:

  | Case | Status | `X-Proxy-Outcome` | `X-Proxy-Code` |
  |---|---|---|---|
  | `X-Proxy-Url` missing, empty, repeated or invalid; `X-Proxy-TimeoutMs` repeated or invalid (it is optional); malformed marker; a forwarded header value that is not ASCII (answered before any tokenizer call) | 400 | NotSent | `MARKER_SYNTAX` |
  | Tokenizer rejects the token, or the raw value has characters outside the allowed set | 422 | NotSent | `TOKEN_INVALID` |
  | Tokenizer unreachable or failing after the bounded retries, or answering with a status that is not retried | 503 | NotSent | `TOKENIZER_UNAVAILABLE` |
  | No usable connection came to be, and nothing of the request was written to the provider: DNS failure, connection refused or unreachable, the operating system's own connect timeout (about 60-75 s: 75 s is `net.inet.tcp.keepinit` on macOS, and 60.1 s was measured here on a VPN route), TLS failure | 502 | NotSent | `PROVIDER_UNREACHABLE` |
  | An `X-Proxy-TimeoutMs` expiry (also while connecting), or a reset, a close or a broken answer after the request was written | 504 | Unknown | `PROVIDER_NO_RESPONSE` |
  | Anything else | 500 | Unknown | `PROXY_ERROR` |

  `NotSent` means the provider cannot have seen the request; `Unknown` (`504`, `500`) means it may have.
  A request with an unresolved marker never reaches a provider (except as under Known limits).

  The default `X-Proxy-TimeoutMs` (100 s), like the 90 s the adapters in `on` send, is longer than the
  operating system's connect timeout (about 60-75 s, above), so by default the operating system's timeout
  wins (`502` for a host that never completes the connection), while any `X-Proxy-TimeoutMs` below it gives
  `504` for the same host.

  `MARKER_SYNTAX` is reused for the non-ASCII header value because nothing better exists in the PDR's
  table: the provider-side client cannot write such a value, so nothing would be sent. The `title` of the
  problem is the code, and its `detail` a fixed sentence that never holds a token, a raw value or any part
  of the request.
- **Logging and storage.** One line per request:

  ```
  send provider=<id> method=<method> host=<authority> path=<path> status=<status> outcome=<outcome> code=<code|-> error=<ExceptionTypeName|-> tokenizer=<ms|-> provider=<ms|->
  ```

  The provider id is shown only when it looks like an id (else `-`). `host` and `path` are those of the
  original `X-Proxy-Url`, with every marker shown as `<marker>`: the log never shows a marker's token, and
  a URL with a malformed or double-encoded marker shows `-` for both. `error` is set only for an
  unexpected exception (one that is not a deliberate proxy error): its type name, never its message. It
  is `-` otherwise, so a deliberate `PROXY_ERROR` logs `code=PROXY_ERROR error=-`. The durations are `-`
  for a step that did not run. Never a token, a raw value, a query string, a header or a body. The proxy
  stores and caches nothing.
- **Profiles**, in the proxy's `appsettings.json` under `Providers`, keyed by provider id:
  `plain-provider` has no entry, so it gets the plain profile (replace markers and send).
  `encrypted-provider` has `{"Hook":"encrypt","TrustedCertificate":"../../certs/sandbox.crt"}`. The
  `encrypt` hook always runs after the replacement, never before: the body, decoded as UTF-8 (invalid
  bytes are replaced), becomes `{"payload":"<PayloadCipher.Encrypt(body)>"}` with
  `Content-Type: application/json` and the length recomputed. `TrustedCertificate` is applied like the
  adapters' (custom root, host name still verified); a provider without an entry, or an entry without a
  `TrustedCertificate`, uses the system trust. An unknown `Hook` name, or a `TrustedCertificate` that does
  not exist, stops the proxy at startup (message on stderr, exit code 1). The mTLS profile and signing
  hooks are out of scope.
  - Provider ids and hook names match case-insensitively (`Encrypted-Provider` gets the hook too).
  - The `encrypt` hook is skipped when the request has no body: a `GET` stays a `GET` with no body (the
    hook adds neither a body nor a content type).
  - An empty `Hook` value means no hook.

### Tokenizer

- The sandbox's invalid token `Y000000000000000` answers `404 {"error":"token not found"}` so that
  `TOKEN_INVALID` can be tested. Every other token still returns the constant.

### `PciProxy:Mode` `on`

- `PciProxy:Mode` becomes `off`, `egress` or `on`. `shadow` is still rejected at startup, with the same
  kind of message (`PciProxy:Mode must be 'off', 'egress' or 'on' (got '...')`).
- `on` adds, in each adapter's `appsettings.json`: `PciProxy:ProxyUrl` (`http://127.0.0.1:5700`),
  `PciProxy:TenantId` (`sandbox`) and `PciProxy:ProviderId` (`plain-provider` for the PlainAdapter,
  `encrypted-provider` for the EncryptedAdapter). In `on` they are validated at startup (`ProxyUrl` an
  absolute http or https URL, the ids neither empty nor blank), and so is `PciProxy:ForwarderUrl` (an
  absolute http URL, as in `egress`), which `on` needs for requests without a marker. A setting that fails
  the check makes the adapter print a message that names it and exit with code 1. The adapter logs
  `PciProxy mode=on proxy=<url> tenant=<id> provider=<id> forwarder=<url>` when it starts.
- **No tokenizer in `on`.** The adapters use `{{pci:ba:Y000000000000001}}` where the raw account number
  was: in the create body, and URL-encoded (`Uri.EscapeDataString`) in the PlainAdapter's validate query
  string. The EncryptedAdapter sends plaintext JSON containing the marker, for create and validate: the
  proxy's hook encrypts it, so the adapter does not.
- **`PciProxyHandler`**, a `DelegatingHandler` in `Sandbox.Shared` on the provider `HttpClient`. A request
  in whose URL, headers or body anything starts like a marker is re-sent to
  `<ProxyUrl>/api/t/<TenantId>/providers/<ProviderId>/send` with the same method, headers, body bytes and
  content type, plus `X-Proxy-Url: <the original absolute URL>` and, unless the request has an
  `X-Proxy-TimeoutMs` of its own, `X-Proxy-TimeoutMs: 90000`. (A named constant: 90 s is below the 100 s
  default timeout of the adapter's `HttpClient`, so the proxy's `504 PROVIDER_NO_RESPONSE` for a provider that
  never answers arrives before the adapter's own timeout.) The handler detects the single-level forms of a
  marker, raw or encoded, which is the proxy's scanner rule (`PciMarker.ContainsMarker`), so a malformed
  marker in those forms gets the proxy's `MARKER_SYNTAX` and never goes direct. It does not detect a
  double-encoded marker in the URL (`%257B%257Bpci...`): the adapter would not re-send such a request but
  send it on, as a request without a marker, through the Forwarder (the adapters never build one), and the
  proxy rejects such a URL if it ever receives one. The re-sent request goes out on a client that never uses
  the Forwarder, follows no redirect and keeps no cookie, and the proxy's response is returned
  unchanged, including the proxy's own errors and a `3xx` (the PDR's mapping of those errors to exceptions
  is out of scope). As always, an adapter's answer to its own caller carries only the status, the body and
  the content type, so `X-Proxy-Outcome` and `X-Proxy-Code` show only on a direct call to the proxy. A
  request without a marker continues to the primary handler, which sends it through the Forwarder as in
  `egress`.
- **Adapter log in `on`.** The provider client's outbound log shows the logical request
  (`--> POST http://plain-provider.localhost:5200/...`, whatever the request is re-sent to) and then the
  real call (`--> POST http://127.0.0.1:5700/api/t/sandbox/providers/<id>/send`), each with its `<--`
  line. The pairs are nested, not sequential: `-->` logical, `-->` real, `<--` real, `<--` logical. The
  validate `GET` has no body, so on the second line its marker is only in the `X-Proxy-Url`
  header, which the log does not print (the first line shows it in the query string).
- `off` and `egress` are unchanged.

### Scripts

- `scripts/run-all.sh` starts the PciProxy as `[PciProxy]` (after the tokenizer; six apps plus the
  Forwarder), so `PciProxy__Mode=on scripts/run-all.sh` runs the whole `on` flow.
- `scripts/smoke.sh` gets a `pci-proxy` group that calls the send endpoint directly (with a `check_proxy`
  helper that also matches `X-Proxy-Outcome` and `X-Proxy-Code`), an invalid-token check in the
  `tokenizer` group, and the `forwarder` group learns `on` (see Verification).
  `PCI_PROXY_MODE=on scripts/smoke.sh` runs it for the `on` flow.

### Verification

- Every existing adapter check passes unchanged in `on` mode, so the suite is run in all three modes.
- The `pci-proxy` group (direct calls to the proxy): `validate-transaction` and `create-transaction` with
  a marker, through `encrypted-provider`, return `true` and `accepted`, which shows the encryption: the
  encrypted provider answers `200` only to a properly encrypted body, so it is the oracle for the `encrypt`
  hook, and only for that, since it accepts any non-null `bankAccountNumber`, the marker included. No smoke
  row proves that the markers are replaced, or that the hook runs after the replacement: the plain provider
  answers `200` to any body and any query, so a plain-provider create with a marker in the body and a
  validate with the marker in the query string (both `200`) show only that the request was accepted.
  Replacement, and replacement before encryption, are shown by the one-off captures below. The group also
  checks that a provider's own `404` and `405` pass through as `Forwarded`, and that every error row above
  that needs nothing but the running stack returns its status, `X-Proxy-Outcome` and
  `X-Proxy-Code` (invalid token, malformed marker in its raw, encoded and partly encoded forms, a missing
  `X-Proxy-Url`, a bad `X-Proxy-TimeoutMs`, a non-ASCII header, an unreachable provider, and the plain
  profile failing the TLS handshake with the self-signed provider). The `503` and `504` rows need a
  stopped tokenizer and a provider that never answers, so they are one-off checks (below).
- With `PCI_PROXY_MODE=on` the `forwarder` group's log checks expect the plain adapter's create and
  validate requests to be absent from the Forwarder log (they go to the PCI Proxy; the validate pattern
  has no query string, since the adapter never has the raw number), its balance request present (it has no
  marker), a `CONNECT encrypted-provider` present (the group now also calls the encrypted adapter's
  balance), and neither the tokenizer nor the PCI Proxy in the log.
- One-off checks, not in the smoke script:
  - a raw capture (a python server standing in for the plain provider) shows the raw account number in
    the create body and in the validate query string, no marker left and no `X-Proxy-*` header, while the
    marker form is what arrives at the proxy;
  - a raw capture of what the encrypted provider receives (a TLS server with the sandbox certificate
    standing in for it, reached through `encrypted-provider`): the payload, decrypted with
    `openssl enc -d -aes-256-cbc` and the sandbox key and IV, holds the raw account number, so the hook ran
    after the replacement, and no marker is left anywhere in the request;
  - with the PCI Proxy stopped in `on`, an adapter's create fails and the tokenizer log gains no line (the
    adapter never calls the tokenizer);
  - with the tokenizer stopped, the proxy answers `503 TOKENIZER_UNAVAILABLE` after its retries;
  - a provider that does not answer within `X-Proxy-TimeoutMs` gives `504 PROVIDER_NO_RESPONSE`; through an
    adapter in `on`, which sends `X-Proxy-TimeoutMs: 90000`, a provider that accepts the connection and never
    answers gives the proxy's `504` after about 90 s (90.2 s measured; without the header the adapter's own
    100 s timeout fired first, a framework `500` after 100.1 s, logged by the proxy as a caller abort);
  - the proxy's log contains no token, raw value, query string or body;
  - `shadow` is rejected at startup.

### Out of scope for stage 3

- Authentication of the caller, dry run, the digest and `pci-proxy:shadow`, response scrubbing, the
  `mtls` profile and the signing hooks, SOAP and WCF, the WillCall Gateway, and the adapters' mapping of
  proxy errors to exceptions and retries.

### Known limits

What the proxy does not do, as it stands (the sandbox leaves these as they are):

- A provider's own `X-Proxy-*` response headers pass through, next to the proxy's `X-Proxy-Outcome`.
- A body that the byte scan cannot read, one with a gzip or brotli `Content-Encoding` or a UTF-16 body,
  carries its markers through unresolved (the proxy neither decompresses nor transcodes).
- A marker in the authority of `X-Proxy-Url` (the host part) is replaced like any other.
- Headers named in `Connection` are not removed; only the fixed hop-by-hop list is.
- A marker in a URL path is logged as `<marker>`, never as its token, but the rest of the path is logged
  as it is.

### Points confirmed

The PDR author confirmed these points when the spec was approved.

1. **Provider ids** `plain-provider` and `encrypted-provider`, and tenant id `sandbox`.
2. **One generic marker replacement** (URL, headers, body) instead of a separate per-provider hook for
   the plain adapter's query string.
3. **The allowed raw-value set** is 1 to 64 ASCII letters or digits.
4. **The sandbox tokenizer's invalid token** is `Y000000000000000`.
5. **Bounded retries:** 3 attempts, 100 ms apart.
6. **The proxy calls the tokenizer and the providers directly**, not through the Forwarder (Figure 1).
7. **An `X-Proxy-TimeoutMs` expiry is `504 PROVIDER_NO_RESPONSE`, Unknown**, even if it happened while
   connecting. A connect failure that the operating system reports (refused, unreachable, its own connect
   timeout, about 60-75 s: 75 s is `net.inet.tcp.keepinit` on macOS, and 60.1 s was measured here on a VPN
   route) is `502 NotSent PROVIDER_UNREACHABLE`, since no usable connection existed and nothing was written.
8. **Requests without a marker in `on` go through the Forwarder.**

## Confirmed decisions

1. **Adapters call the tokenizer, not the providers** (in `off` and `egress`; in `on`, stage 3, the PCI
   Proxy calls the tokenizer and the adapters do not). Each adapter gets the raw account number from
   the tokenizer, then has everything it needs to call its provider. The providers call nothing.
2. **The PCI marker is out of the sandbox.** The first iteration shows the current state, where there
   are no markers. They are introduced later (in stage 3).
3. **Fixed transaction IDs that name the sending adapter.** Every call sends `T-001-plain` from the
   plain adapter and `T-002-encrypted` from the encrypted adapter.
4. **The cipher is AES-CBC with a fixed key and IV.** It is unauthenticated, so a wrong payload is
   caught by the padding check and the `Transaction` validation.
5. **Classic `.sln`** instead of the .NET 10 default `.slnx`. **.NET 10** as the target framework.
6. **Envoy runs as a Podman container.** The image `docker.io/envoyproxy/envoy:v1.36.9` is already in
   the local Podman storage.
7. **Both HTTP and HTTPS are covered.** The PlainProvider (HTTP) is forwarded as plain requests and the
   EncryptedProvider (HTTPS) through a `CONNECT` tunnel.
8. **No response scrubbing and no dry run in the PCI Proxy.** It always sends the request and returns the
   provider's response as it is (the digest and `X-Proxy-DryRun` of the PDR are out of scope).
9. **`shadow` stays rejected.** An adapter whose `PciProxy:Mode` is `shadow` stops at startup, as it does
   for any value other than `off`, `egress` or `on`.

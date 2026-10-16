# PCI Proxy Sandbox — Design

Date: 2026-10-16
Status: approved by the PDR author (v2 plus the transaction-ID and `.sln` decisions below)
Source: PDR "Adapter PCI Proxy" (Confluence, Money Transfer space), Figure 1.

## Purpose

A local sandbox of the part of Figure 1 that exists today: **adapter → tokenizer** and
**adapter → provider**. The PCI Proxy and the WillCall Gateway are not in it. Stage 2 (below) adds the
Forwarder.

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

- PCI Proxy, WillCall Gateway, the `pci-proxy:shadow` and `pci-proxy:on` modes, PCI markers,
  `X-Proxy-*` headers. (The Forwarder and the `off` and `egress` modes arrive in stage 2.)
- Authentication: every endpoint in every application is anonymous.
- Retries, timeouts, and TLS other than the encrypted provider's self-signed certificate (stage 2).
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
  Any other value, including the later `shadow` and `on`, stops the adapter at startup with a clear
  message. In `egress` an empty or non-absolute-http `PciProxy:ForwarderUrl` stops the adapter at
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

- Every existing adapter check in `scripts/smoke.sh` passes unchanged in both modes: the suite is run
  once per mode.
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
3. **Tokenizer calls stay direct** in both modes.
4. **`shadow` and `on` are rejected at startup** for now.
5. **A missing Podman only warns**: `run-all.sh` carries on and the sandbox works in `off` mode.

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
6. **Envoy runs as a Podman container.** The image `docker.io/envoyproxy/envoy:v1.36.9` is already in
   the local Podman storage.
7. **Both HTTP and HTTPS are covered.** The PlainProvider (HTTP) is forwarded as plain requests and the
   EncryptedProvider (HTTPS) through a `CONNECT` tunnel.

# pdr-pi-pci-proxy

A local sandbox for the PDR "Adapter PCI Proxy". An adapter either detokenizes a bank account itself and sends the **raw** number to its provider
(`off`), or puts a **marker** `{{pci:ba:<token>}}` in its request and lets the **PCI Proxy** detokenize it on the way out (`on`). The Will Call
adapter returns markers in `on`, and the PCI Proxy replaces them in the response. You drive it with curl and read the logs.

| App | Port | Role |
|---|---|---|
| Tokenizer | 5100 | `POST /detokenize` `{"token":"..."}` → `{"value":"012345678901234567"}` for every token except `Y000000000000000` (invalid → `404`) |
| PlainProvider | 5200 | Logs what it receives; `validate-transaction` takes the raw number in the query string |
| EncryptedProvider | 5300 | HTTPS (self-signed); accepts only an encrypted `{"payload":"..."}`, else `400` |
| PlainAdapter / EncryptedAdapter | 5400 / 5500 | `POST /create-transaction`, `POST /validate-transaction`, `GET /balance` |
| WillCallAdapter | 5800 | `GET /lookup-transactions`: two hard-coded transactions, tokens `Y000000000000002` / `Y000000000000003`; Swagger at `/swagger` |
| Forwarder | 5600 (proxy), 5601 (admin) | Envoy in the Podman container `sandbox-forwarder`: forward proxy, no logic |
| PciProxy | 5700 | `/api/t/{tenant}/providers/{provider}/send` (replaces markers, calls the provider) and `/will-call/**` (YARP to the WillCallAdapter, replaces markers in responses) |

Needs the .NET 10 SDK, `openssl` and `jq`. The Forwarder needs Podman with its machine running; without it the sandbox still works in `off`
(`run-all.sh` only warns). Every endpoint is anonymous.

## Demo

Two setups, run **one at a time** (same ports). Each is `scripts/run-all.sh` with `PciProxy__Mode` set; stop with Ctrl+C.

| Terminal 1 | Terminal 2 | Adapters |
|---|---|---|
| `scripts/demo-off.sh` | `scripts/demo-curl.sh off` | call the tokenizer, send the **raw** number to the providers; Will Call returns **raw** numbers |
| `scripts/demo-on.sh` | `scripts/demo-curl.sh on` | send a **marker** to the PCI Proxy, which detokenizes (and encrypts, for the encrypted provider); Will Call returns **markers** |

`demo-curl.sh` runs the curls below with a note per call. Every call sends `X-Request-Id: demo-<case>`.

```bash
# PlainAdapter and EncryptedAdapter (both modes)
curl -i -X POST http://plain-adapter.localhost:5400/create-transaction     -H 'X-Request-Id: demo-plain-create'
curl -i -X POST http://plain-adapter.localhost:5400/validate-transaction   -H 'X-Request-Id: demo-plain-validate'
curl -i         http://plain-adapter.localhost:5400/balance                -H 'X-Request-Id: demo-plain-balance'
curl -i -X POST http://encrypted-adapter.localhost:5500/create-transaction   -H 'X-Request-Id: demo-enc-create'
curl -i -X POST http://encrypted-adapter.localhost:5500/validate-transaction -H 'X-Request-Id: demo-enc-validate'
curl -i         http://encrypted-adapter.localhost:5500/balance              -H 'X-Request-Id: demo-enc-balance'

# Will Call, directly: raw numbers in `off`, markers ("{{pci:ba:Y000000000000002}}") in `on`
curl -i http://will-call-adapter.localhost:5800/lookup-transactions -H 'X-Request-Id: demo-willcall-direct'
# Will Call through the PCI Proxy: raw numbers in both modes
curl -i http://pci-proxy.localhost:5700/will-call/lookup-transactions -H 'X-Request-Id: demo-willcall-proxy'

# Will Call Swagger, served by the PCI Proxy (open the first URL in a browser; its document names /will-call as the server)
#   http://pci-proxy.localhost:5700/will-call/swagger
curl -sS http://pci-proxy.localhost:5700/will-call/swagger/v1/swagger.json | jq '{title: .info.title, servers, paths: (.paths | keys)}'
```

What to see: in `off` the adapter calls the Tokenizer, and the provider logs the raw `bankAccountNumber`. In `on` the adapter's `out` line carries a
marker and is re-sent to the PCI Proxy (`.../providers/<id>/send`); the Tokenizer call comes from the PCI Proxy; the provider still receives the raw
number (the EncryptedProvider logs the `decrypted` transaction: the proxy encrypted it); `balance` has no marker and goes through the Forwarder.

### All logs in one output, sorted by time

`run-all.sh` prints every app's output with an `[App]` prefix as it arrives, and writes each app's lines to `logs/<App>.log` (git-ignored, reset at
every start). `scripts/logs.sh` merges them, sorted by timestamp:

```bash
scripts/logs.sh                          # everything
scripts/logs.sh --id demo-plain-create   # one request through every app
scripts/logs.sh --app PciProxy           # one app
scripts/logs.sh --json | jq .            # raw JSON lines, sorted
```

```
20:33:57.003 PlainAdapter  [demo-plain-create] out method=POST url=http://127.0.0.1:5700/api/t/sandbox/providers/plain-provider/send body={...}
20:33:57.110 Tokenizer     [demo-plain-create] in from=127.0.0.1:57149 method=POST target=/detokenize body={"token":"Y000000000000001"}
20:33:57.192 PlainProvider [demo-plain-create] in from=127.0.0.1:57155 method=POST target=/create-transaction body={...}
20:33:57.200 PciProxy      [demo-plain-create] send from=127.0.0.1:57148 provider=plain-provider ... status=200 outcome=Forwarded
```

## Modes

`PciProxy:Mode` in an adapter's `appsettings.json` (or `PciProxy__Mode`; set it for the whole sandbox with `PciProxy__Mode=on scripts/run-all.sh`):

| Mode | The adapter |
|---|---|
| `off` (default) | detokenizes through the Tokenizer and calls the provider directly with the raw number |
| `egress` | the same, but every provider call goes through the Forwarder |
| `on` | never calls the Tokenizer; puts a marker where the raw number was and re-sends the request to the PCI Proxy; a request without a marker (balance) goes through the Forwarder |

```
on, with a marker:     adapter ─► PciProxy ─► Tokenizer (one call per distinct token)
                                         └──► provider (raw value; encrypted for encrypted-provider)
on, without a marker:  adapter ─► Forwarder ─► provider
```

## PCI Proxy send endpoint

`/api/t/{tenantId}/providers/{providerId}/send` on `http://127.0.0.1:5700`, any HTTP method (the tenant is ignored). The method, headers and raw body
are what the provider receives, with markers replaced (percent-encoded in the URL, as they are in headers and bodies otherwise). Headers:
`X-Proxy-Url` (**required**: the provider's absolute URL, markers allowed) and `X-Proxy-TimeoutMs` (default 100000). The proxy never retries, follows no
redirect and keeps nothing. `providerId` picks a profile (`Providers` in `src/PciProxy/appsettings.json`): `encrypted-provider` encrypts the body and pins the
sandbox certificate, anything else is plain.

```bash
curl -i -X POST http://127.0.0.1:5700/api/t/sandbox/providers/plain-provider/send \
  -H 'X-Proxy-Url: http://plain-provider.localhost:5200/create-transaction' -H 'Content-Type: application/json' \
  -d '{"transactionId":"T-900","amount":1.00,"currency":"USD","senderName":"A","recipientName":"B","bankAccountNumber":"{{pci:ba:Y000000000000001}}"}'
```

Every response carries `X-Proxy-Outcome`. A failure of the proxy itself is `application/problem+json` with `X-Proxy-Code`:

| Case | Status | `X-Proxy-Outcome` | `X-Proxy-Code` |
|---|---|---|---|
| bad `X-Proxy-Url`/`X-Proxy-TimeoutMs`, malformed marker | 400 | `NotSent` | `MARKER_SYNTAX` |
| tokenizer rejects a token | 422 | `NotSent` | `TOKEN_INVALID` |
| tokenizer unreachable (3 attempts) | 503 | `NotSent` | `TOKENIZER_UNAVAILABLE` |
| provider unreachable, nothing written | 502 | `NotSent` | `PROVIDER_UNREACHABLE` |
| timeout, or connection broke after the write | 504 | `Unknown` | `PROVIDER_NO_RESPONSE` |
| anything else | 500 | `Unknown` | `PROXY_ERROR` |

A provider's own `4xx`/`5xx` passes through with `X-Proxy-Outcome: Forwarded`. A request with an unresolved marker never reaches the provider.
Try it with the invalid token `{{pci:ba:Y000000000000000}}` (`422 TOKEN_INVALID`).

## Will Call through the PCI Proxy

`/will-call/**` is a YARP route (`Yarp.ReverseProxy`, `ReverseProxy` in `src/PciProxy/appsettings.json`) to the WillCallAdapter with the prefix removed.
Every 2xx JSON response has the markers in its string values replaced with raw values from the Tokenizer; other responses stream through. A rejected token,
an unavailable tokenizer or a malformed marker makes the whole response `500` with `X-Proxy-Code`. A request with marker syntax in its path, query,
headers or body is refused with `400 MARKER_SYNTAX`. The proxy sends `X-Forwarded-Prefix: /will-call`, so the adapter's Swagger names `/will-call` as its server.
Not done: authentication, dropping only the unresolved items of a list, a body size limit.

## Logs

One JSON object per line in every app and in the Forwarder: `ts` (UTC), `category`, `requestId`, `traceId`, `message` (the template of the entry) and one field
per value (`origin`, `method`, `status`, ...). No log level. `requestId` and `traceId` are on every line written while a request is handled. The request
id is made up by the first app that sees the request, or taken from a valid `X-Request-Id` you send (1 to 64 letters, digits, `.`, `-`, `_`), and passed on in
that header on every call; the trace id is the W3C one that .NET passes on in `traceparent`. `from`/`origin` is the peer's address (the Forwarder runs in a
container, so it shows the Podman gateway `192.168.127.1`). For readable text instead of JSON: `Logging__Console__FormatterName=sandbox-text`.

## Checks and certificate

`scripts/smoke.sh [group ...]` runs curl checks against the running apps (groups: `tokenizer`, `plain-provider`, `encrypted-provider`, `plain-adapter`,
`encrypted-adapter`, `forwarder`, `pci-proxy`, `will-call`); `PCI_PROXY_MODE=off|egress|on` must name the mode the apps run in. `scripts/gen-cert.sh` (called by
`run-all.sh`) creates a self-signed `certs/sandbox.crt` for the encrypted provider; nothing is added to any trust store. Call that provider with
`curl --cacert certs/sandbox.crt https://encrypted-provider.localhost:5300/balance`.

`*.localhost` names resolve to loopback on macOS. The Forwarder container cannot see the Mac's loopback, so `scripts/forwarder.sh` maps `plain-provider.localhost`
and `encrypted-provider.localhost` to the Mac host inside it; the Tokenizer is never called through the Forwarder.

## Known limits

- A body the proxy's byte scan cannot read (gzip/brotli `Content-Encoding`, UTF-16) carries its markers through unresolved.
- Headers named in `Connection` are not removed (only the fixed hop-by-hop list is).
- The cipher is AES-CBC with a fixed dummy key and IV: it only gives the encrypted provider something to reject. Never reuse it.

Layout: `src/PciProxy` (the proxy), `src/Sandbox.Shared` (logging, marker, cipher, adapter-side proxy handler), `forwarder/envoy.yaml`, `scripts/`. The design and plans are
in `docs/superpowers/`.

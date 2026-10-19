# pdr-pi-pci-proxy

A local sandbox for the PDR "Adapter PCI Proxy". It reproduces **today's** flow from Figure 1: an
adapter detokenizes a bank account through the tokenizer and sends the **raw** account number to a
provider. It also has the PDR's **Forwarder** (an Envoy proxy) and the **PCI Proxy**. With
`PciProxy:Mode=egress` the adapters send their provider calls through the Forwarder. With
`PciProxy:Mode=on` they stop detokenizing: an adapter puts a marker where the raw account number was and
sends the request to the PCI Proxy, which detokenizes it and calls the provider. The WillCall Gateway is
not part of it.

You call the adapters (or the PCI Proxy) with curl and watch each app's console to see how a request
travels.

| App | Port | Role |
|---|---|---|
| Tokenizer | 5100 | `POST /detokenize` takes `{"token":"..."}` and answers `{"value":"012345678901234567"}`, the raw account number, for every token except `Y000000000000000`, the sandbox's invalid token, which gets `404 {"error":"token not found"}` |
| PlainProvider | 5200 | `POST /create-transaction` logs whatever it receives; `GET /validate-transaction?bankAccountNumber=...` always answers `true` (the raw account number travels in the query string); `GET /balance` |
| EncryptedProvider | 5300 | **HTTPS only**, with a self-signed certificate. `POST /create-transaction` and `POST /validate-transaction` expect `{"payload":"<base64>"}`; they answer `400` if the payload is not a properly encrypted transaction (create) or `{"bankAccountNumber":"..."}` (validate); validate answers `true`; `GET /balance` |
| PlainAdapter | 5400 | `POST /create-transaction`, `POST /validate-transaction` and `GET /balance`, calls PlainProvider at `http://plain-provider.localhost:5200` |
| EncryptedAdapter | 5500 | `POST /create-transaction`, `POST /validate-transaction` and `GET /balance`, calls EncryptedProvider at `https://encrypted-provider.localhost:5300` |
| WillCallAdapter | 5800 | `GET /lookup-transactions` returns two hard-coded transactions (`T-101-willcall`, `T-102-willcall`) whose stored bank account tokens are `Y000000000000002` and `Y000000000000003`; Swagger at `/swagger`. `PciProxy:Mode=off` (or `egress`): detokenizes them itself and returns the raw numbers. `on`: returns markers `{{pci:ba:<token>}}` and never calls the tokenizer |
| Forwarder | 5600, 5601 | Envoy 1.36.9 in the Podman container `sandbox-forwarder`: a forward proxy that adds no headers and has no logic. 5600 is the proxy, 5601 its admin interface (`curl http://127.0.0.1:5601/ready`). The adapters use it in `egress` mode, and in `on` mode for requests without a marker |
| PciProxy | 5700 | The PDR's PCI Proxy: `/api/t/{tenantId}/providers/{providerId}/send`, any HTTP method. It replaces the markers `{{pci:ba:<token>}}` in a request with the raw values from the tokenizer and calls the provider. The adapters use it in `on` mode |

Every endpoint is anonymous. Requires the .NET 10 SDK and `openssl` (it creates the certificate).
The Forwarder, and so the `egress` mode (and the `on` mode, for requests without a marker such as
balance), also needs Podman with its machine running (the image `docker.io/envoyproxy/envoy:v1.36.9` is
fetched by Podman if it is not in local storage yet). In `off` mode the Forwarder is optional: without
Podman the rest of the sandbox works.

## Run

```bash
scripts/run-all.sh
```

This creates the sandbox certificate if it is missing, builds once, starts the Forwarder container
and starts all six apps; each log line is prefixed with the app name, `[Forwarder]` for the
container's log. Ctrl+C stops everything and removes the container, except that an interrupt during the
Forwarder's first couple of seconds, while it is still starting, can leave it running (the next
`run-all.sh` removes it first). If Podman is not installed or its machine is not running, `run-all.sh`
prints a warning (`warning: Forwarder not started (...)`) and carries on without the Forwarder. In
another terminal:

```bash
curl -i -X POST http://127.0.0.1:5400/create-transaction
curl -i -X POST http://127.0.0.1:5400/validate-transaction
curl -i http://127.0.0.1:5400/balance
curl -i -X POST http://127.0.0.1:5500/create-transaction
curl -i -X POST http://127.0.0.1:5500/validate-transaction
curl -i http://127.0.0.1:5500/balance
```

The create and validate endpoints take no body. Each adapter sends a hard-coded transaction on create
(`T-001-plain` from the plain adapter, `T-002-encrypted` from the encrypted one). On validate it
detokenizes the same placeholder token and sends the raw account number to its provider: in the query
string of a `GET` for the plain provider, inside the encrypted payload of a `POST` for the encrypted one.
That is the `off` mode, the default. The other two modes are below.

### HTTPS and names

`scripts/gen-cert.sh` creates `certs/sandbox.crt` and `certs/sandbox.key` once (a self-signed certificate
for `encrypted-provider.localhost`, `localhost` and `127.0.0.1`). `certs/` is git-ignored and nothing is
added to any system or keychain trust store. The EncryptedProvider serves it, and the EncryptedAdapter
trusts exactly that file (`Provider:TrustedCertificate`) and checks the host name; any other
certificate is rejected. The PCI Proxy pins the same file for the encrypted provider (see Profiles
below). To call the provider yourself:

```bash
curl --cacert certs/sandbox.crt https://127.0.0.1:5300/balance
```

The adapters reach the providers by `plain-provider.localhost` and `encrypted-provider.localhost`.
macOS resolves every `*.localhost` name to loopback, so direct calls (and the PCI Proxy's calls) need
nothing. The Forwarder container cannot see the Mac's loopback, so `scripts/forwarder.sh` gives it the
same two names, pointing at the Mac host (the Forwarder and the adapters have to agree on the provider
names). The tokenizer stays at `http://127.0.0.1:5100` and is never called through the Forwarder.

## The modes: `off`, `egress` and `on`

`PciProxy:Mode` in an adapter's `appsettings.json` (or `PciProxy__Mode` in the environment, in any
casing) says how the adapter reaches its provider:

| Mode | What the adapter does |
|---|---|
| `off` (default) | Detokenizes the account number through the tokenizer and calls the provider directly with the raw number. Today's flow. |
| `egress` | The same, but every provider call (create, validate and balance) goes through the Forwarder at `PciProxy:ForwarderUrl` (`http://127.0.0.1:5600`). |
| `on` | Never calls the tokenizer. It puts the marker `{{pci:ba:Y000000000000001}}` where the raw account number was. A request that carries a marker is re-sent to the PCI Proxy, which detokenizes it and calls the provider; a request without one (balance) goes through the Forwarder, as in `egress`. |

Any other value, `shadow` included, stops the adapter at startup with
`PciProxy:Mode must be 'off', 'egress' or 'on' (got '<value>')`. Each adapter logs `PciProxy mode=...`
when it starts. To start the whole sandbox in a mode, set the variable for `run-all.sh`; the default is
`off`:

```bash
PciProxy__Mode=egress scripts/run-all.sh
```

```bash
PciProxy__Mode=on scripts/run-all.sh
```

### The Forwarder: `egress`

The plain provider's HTTP requests are forwarded as they are; the encrypted provider is reached through a
`CONNECT` tunnel, so the TLS session still ends at the provider. Without a running Forwarder, provider
calls in `egress` mode fail. `scripts/forwarder.sh start|stop|logs` manages the container on its own.

### The PCI Proxy: `on`

```
on, request with a marker:
  adapter ─► PciProxy :5700 ─► Tokenizer :5100      (one detokenize call per distinct token)
                         └───► provider             (direct, raw value; encrypted for encrypted-provider)
on, request without a marker (balance):
  adapter ─► Forwarder :5600 ─► provider
```

With the `on` stack running, the same curl commands as above work. What changes is what the adapter sends:
the create body has the marker where the raw account number was, the PlainAdapter's validate query
string carries it URL-encoded (`bankAccountNumber=%7B%7Bpci%3Aba%3AY000000000000001%7D%7D`), and the
EncryptedAdapter sends plain JSON that contains the marker, for create and validate: it does not encrypt,
because the proxy's `encrypt` hook does. The adapter's provider call (the same method, headers, body and
content type) is re-sent to
`http://127.0.0.1:5700/api/t/sandbox/providers/<provider id>/send` (`PciProxy:ProxyUrl`,
`PciProxy:TenantId`, and `PciProxy:ProviderId`: `plain-provider` for the PlainAdapter,
`encrypted-provider` for the EncryptedAdapter), with the provider's original URL in `X-Proxy-Url`, and
`X-Proxy-TimeoutMs: 90000` unless the request has an `X-Proxy-TimeoutMs` of its own. 90 s is below the 100 s
default timeout of the adapter's own `HttpClient`, so a provider that never answers gives the proxy's `504`
after about 90 s, not the adapter's `500` after 100 s. The proxy's status, body and content type, an error's
included, are what the adapter returns (a PCI Proxy that cannot be reached at all gives the framework's `500`).
In `on` mode these three settings and
`PciProxy:ForwarderUrl` are checked at startup (absolute URLs, non-empty ids); a bad one stops the adapter
with a message that names it.

### The send endpoint

You can call the PCI Proxy yourself. It is `/api/t/{tenantId}/providers/{providerId}/send` on
`http://127.0.0.1:5700`, for any HTTP method. `tenantId` is ignored (the adapters send `sandbox`);
`providerId` selects the provider's profile (below).

| Request header | |
|---|---|
| `X-Proxy-Url` | **Required**, once: the provider's absolute `http` or `https` URL, as the adapter would have called it. It may contain markers. |
| `X-Proxy-TimeoutMs` | Optional: a positive integer (digits only), default `100000` (the adapters in `on` send `90000`). It bounds the whole exchange with the provider. |

The method, the other headers and the raw body are what the provider receives, with the markers replaced.
The proxy does not forward `X-Proxy-*` headers (there is no dry run: `X-Proxy-DryRun` is ignored), `Host`
(it comes from the URL), `Content-Length` (recomputed) or the hop-by-hop headers (`Connection`,
`Keep-Alive`, `Proxy-*`, `TE`, `Trailer`, `Transfer-Encoding`, `Upgrade`). It follows no redirect (a `3xx`
passes through), never retries, does not decompress, keeps no cookies, and connects to the provider
directly, never through the Forwarder.

A marker is `{{pci:ba:<token>}}`, where the token is `Y` plus 15 ASCII letters or digits. The proxy
finds markers in the URL, in header values and in the body, and replaces each with the raw value from the
tokenizer (one call per distinct token): percent-encoded in the URL, unchanged in headers and bodies. Every character
of a marker may be written as itself or as a `%XX` escape, so `%7B%7Bpci%3Aba%3A<token>%7D%7D` and the
partly escaped `%7B%7Bpci:ba:<token>%7D%7D` (what .NET puts on the wire for a URL written with a marker)
work. Only one level of decoding is done: a double-encoded marker in the URL is rejected. In the sandbox
every token answers `012345678901234567`, except `Y000000000000000`, which the tokenizer rejects.

```bash
curl -i -X POST http://127.0.0.1:5700/api/t/sandbox/providers/plain-provider/send \
  -H 'X-Proxy-Url: http://plain-provider.localhost:5200/create-transaction' \
  -H 'Content-Type: application/json' \
  -d '{"transactionId":"T-900","amount":1.00,"currency":"USD","senderName":"A","recipientName":"B","bankAccountNumber":"{{pci:ba:Y000000000000001}}"}'
```

The PlainProvider logs the body with the raw `bankAccountNumber` (`012345678901234567`). The answer is the
provider's (`200 {"status":"accepted"}`) plus the proxy's headers:

- `X-Proxy-Outcome: Forwarded` (the request was sent; a provider's own `4xx` or `5xx` is still `Forwarded`);
- `Server-Timing: tokenizer;dur=<ms>, provider;dur=<ms>` (the tokenizer entry only when something was
  detokenized).

A marker can also be in the URL. This validates through the plain provider, which logs
`GET /validate-transaction?bankAccountNumber=012345678901234567`:

```bash
curl -i 'http://127.0.0.1:5700/api/t/sandbox/providers/plain-provider/send' \
  -H 'X-Proxy-Url: http://plain-provider.localhost:5200/validate-transaction?bankAccountNumber=%7B%7Bpci%3Aba%3AY000000000000001%7D%7D'
```

A request the proxy cannot complete is answered with `application/problem+json`, with
`X-Proxy-Outcome` (on every proxy response) and `X-Proxy-Code`:

| Case | Status | `X-Proxy-Outcome` | `X-Proxy-Code` |
|---|---|---|---|
| `X-Proxy-Url` missing, empty, repeated or invalid; `X-Proxy-TimeoutMs` repeated or invalid (it is optional); a malformed marker; a forwarded header value that is not ASCII (the code is reused: nothing better exists in the PDR's table) | 400 | `NotSent` | `MARKER_SYNTAX` |
| The tokenizer rejects a token (`400`, `404`, `422`), or the raw value is not 1 to 64 ASCII letters or digits | 422 | `NotSent` | `TOKEN_INVALID` |
| The tokenizer cannot be reached or fails (3 attempts, 100 ms apart, each limited to 5 s; a status such as `401` is not retried) | 503 | `NotSent` | `TOKENIZER_UNAVAILABLE` |
| No usable connection to the provider came to be, and nothing of the request was written: DNS, refused, unreachable, the operating system's own connect timeout (see below), a TLS failure | 502 | `NotSent` | `PROVIDER_UNREACHABLE` |
| `X-Proxy-TimeoutMs` ran out (also while connecting), or the connection broke after the request was written | 504 | `Unknown` | `PROVIDER_NO_RESPONSE` |
| Anything else | 500 | `Unknown` | `PROXY_ERROR` |

`NotSent` means the provider cannot have seen the request; `Unknown` (`504`, `500`) means it may have.

The operating system's own connect timeout is about 60-75 s (75 s is `net.inet.tcp.keepinit` on macOS;
60.1 s was measured here, end to end, on a VPN route). The default `X-Proxy-TimeoutMs` (100 s), like the 90 s
the adapters in `on` send, is longer, so by default the operating system's timeout wins and an unreachable
host that never completes the connection gives `502`; with an `X-Proxy-TimeoutMs` below it the same host
gives `504`.

For example, a request whose token the tokenizer rejects:

```bash
curl -i -X POST http://127.0.0.1:5700/api/t/sandbox/providers/plain-provider/send \
  -H 'X-Proxy-Url: http://plain-provider.localhost:5200/create-transaction' \
  -d '{"bankAccountNumber":"{{pci:ba:Y000000000000000}}"}'
```

The proxy answers `422` with `X-Proxy-Outcome: NotSent` and `X-Proxy-Code: TOKEN_INVALID`. A request with a
malformed marker stops before the tokenizer is called, and one with a marker that cannot be replaced never
reaches the provider (except as under Known limits).

### Profiles

The proxy picks a profile by `providerId`, from `Providers` in `src/PciProxy/appsettings.json`:

| Provider id | Profile |
|---|---|
| `plain-provider` | No entry, so the plain profile: replace the markers and send. HTTPS, if any, uses the system's trust. |
| `encrypted-provider` | `{"Hook":"encrypt","TrustedCertificate":"../../certs/sandbox.crt"}`: replace the markers, then the `encrypt` hook turns the body into `{"payload":"<encrypted body>"}` (the sandbox cipher, `Content-Type: application/json`), and the provider's certificate must be the sandbox one (host name checked, as in the adapters). |

Any other provider id also gets the plain profile (ids and hook names match without regard to case, so
`Encrypted-Provider` is the encrypted one), and the self-signed encrypted provider fails through the plain
profile: `502 PROVIDER_UNREACHABLE`. The hook always runs after the replacement, and only when the request
has a body (a `GET` stays a `GET`); the body is read as UTF-8, and an empty `Hook` value means no hook. An
unknown `Hook` name, or a `TrustedCertificate` file that does not exist, stops the proxy at startup.

### What the `[PciProxy]` log shows

One line per request, for example:

```
[PciProxy] 14:16:03 info: PciProxy[0] send provider=plain-provider method=POST host=plain-provider.localhost:5200 path=/create-transaction status=200 outcome=Forwarded code=- error=- tokenizer=79.2ms provider=11.8ms
```

It shows the provider id, the method, the host and the path of the original `X-Proxy-Url` (a marker in the
path shows as `<marker>`, never as its token), the status, the outcome, the code, the type name of an
unexpected exception (`error=`, never its message) and the tokenizer and provider durations (`-` for a
step that did not run). It never shows a token, a raw value, a query string, a header or a body, and the
proxy keeps nothing.

### Known limits

- A provider's own `X-Proxy-*` response headers pass through, next to `X-Proxy-Outcome`.
- A body that the proxy's byte scan cannot read, one with a gzip or brotli `Content-Encoding` or a UTF-16
  body, carries its markers through unresolved.
- A marker in the host part of `X-Proxy-Url` is replaced like any other.
- Headers named in `Connection` are not removed (only the fixed hop-by-hop list is).
- A marker in a URL path is logged as `<marker>`, never as its token, but the rest of the path is logged
  as it is.

## What to look for in the logs

Log format and the ids. A line is `HH:mm:ss Category id=<request id> trace=<trace id> message`: no level and no event id. The message is
`key=value` pairs (a free-text `body=` comes last) of a few kinds: `in` (a request received), `out` and `res` (a call made, and its answer;
the adapters), and the PCI Proxy's `send` and `will-call` summaries. The Forwarder's line has its own shape:
`[time] id=<request id> traceparent=<traceparent> from=...`. `id` and `trace` are on **every** line written while a request is handled, the
framework's (YARP's `Proxying to ...`) included, and absent from startup lines.

- `id` is the request id. The first app that sees a request makes it up (or keeps a valid `X-Request-Id` that you send with curl: 1 to 64
  letters, digits, `.`, `-`, `_`; a bad one is replaced and never logged) and passes it on in `X-Request-Id` to every call it makes. The PCI Proxy
  does not add the header to what the provider receives; the adapter's own `X-Request-Id` goes on with the other headers.
- `trace` is the W3C trace id. .NET starts it for each request and `HttpClient` passes it on in `traceparent`, so it is the same in the adapter, the PCI Proxy,
  the Tokenizer and the provider. Through the PCI Proxy the provider gets the adapter's `traceparent` and `X-Request-Id` as ordinary headers (the proxy's own
  provider client adds none), so the provider's lines show the adapter's ids. The Forwarder logs the `traceparent` of the call it passes on.

`grep <id>` on the whole output of `run-all.sh` shows the path of one request through the adapter, the PCI Proxy, the Tokenizer, the provider and the Forwarder.

Where a request came from: the providers, the Tokenizer, the adapters, the PCI Proxy and the Forwarder log the address and port of the peer that opened
the connection (`from=127.0.0.1:54321`).
It is the peer's address, never an `X-Forwarded-For` value, which a caller could write itself. The Forwarder runs in a container, so it
sees the Podman gateway (`192.168.127.1`), not `127.0.0.1`: a caller on the host cannot be told from another one by that address.

- **PlainAdapter / EncryptedAdapter:** in `off` and `egress`, the call to the tokenizer with the
  placeholder token `Y000000000000001`, the raw account number that comes back, then the call to the
  provider. In `on` there is no tokenizer call: the provider call carries the marker, and is re-sent to the
  PCI Proxy (see below).
- **PlainProvider:** the request body as received, with the raw `bankAccountNumber`. For validate, the
  raw number shows up in the URL: `GET /validate-transaction?bankAccountNumber=...`. In `on` mode that is
  still the raw number: the PCI Proxy put it there.
- **EncryptedProvider:** the decrypted transaction (or `decrypted validation request`), or
  `rejected: <reason>` for a request it could not decrypt. The EncryptedAdapter also logs the
  `plaintext` it encrypted (not in `on`: it sends plain JSON and the proxy encrypts it).
- **Tokenizer:** an `in id=... from=... method=POST target=/detokenize body={"token":...}` line per call. In `on` mode the calls are the PCI Proxy's, one
  per distinct token of a request, and the adapters make none.
- **PciProxy:** one `send id=... from=<ip:port> provider=... status=... tokenizer_ms=... provider_ms=...` line per send request, and one `will-call id=... from=<ip:port> method=... path=... status=...` line per Will Call request (rejected ones too; the query is never logged), plus `will-call-rewrite id=... values=N tokens=M` when markers were replaced.
- **Forwarder:** one access-log line per request (two for a `CONNECT`, see below), in UTC, as
  `[time] id=<request id> from=<ip:port> METHOD host:port/path -> upstream-address status ua=User-Agent`. In `egress` mode:
  - the plain provider's calls show in full, the raw account number included:
    `GET plain-provider.localhost:5200/validate-transaction?bankAccountNumber=012345678901234567 -> 192.168.127.254:5200 200 ua=-`
    (`ua=-` is the adapters' .NET `HttpClient`, which sends no User-Agent; the address is the Mac host as
    the container sees it);
  - the encrypted provider's calls show only as the tunnel,
    `CONNECT encrypted-provider.localhost:5300- -> 192.168.127.254:5300 200 ua=-`: the Forwarder never
    sees the path or body inside the TLS session. A `CONNECT` is logged twice, with the same start time:
    when the tunnel opens and again when it closes. An adapter keeps its tunnel pooled, so the second
    line can appear a minute or more after the call and looks like a new request;
  - in `off` mode none of the adapters' calls appear (only requests you send through the proxy yourself,
    such as the smoke checks' `ua=curl/...` ones);
  - in `on` mode only the requests without a marker appear: the balance calls (the encrypted adapter's
    as a `CONNECT` tunnel). The create and validate calls do not, and neither do the tokenizer or the
    PCI Proxy.

  A single Envoy warning about a connection limit at startup is expected.

In `on` mode, read an adapter's log like this:

- Each request that carries a marker logs two `out` / `res` pairs (same `id=`). The first,
  `out id=... method=POST url=http://plain-provider.localhost:5200/create-transaction body={... "bankAccountNumber":"{{pci:ba:Y000000000000001}}"}`,
  is the logical request, to the provider's own URL, even though the request is re-sent to the proxy. The
  second, `out id=... method=POST url=http://pci-proxy.localhost:5700/api/t/sandbox/providers/plain-provider/send body={...}`, is the real
  call. The pairs are nested, not sequential: `out` logical, `out` real, `res` real, `res` logical.
- For the PlainAdapter's validate `GET` the marker is in the query string of the first line, and in the
  second only in the `X-Proxy-Url` header, which the log does not print: that line is just
  `out id=... method=GET url=http://pci-proxy.localhost:5700/api/t/sandbox/providers/plain-provider/send body=`.
- An adapter returns only the status, the body and the content type of what it received. `X-Proxy-Outcome`
  and `X-Proxy-Code` are not among them: to see them, call the PCI Proxy directly (see the send endpoint).

## Checks

The groups are `tokenizer`, `plain-provider`, `encrypted-provider`, `plain-adapter`,
`encrypted-adapter`, `forwarder` and `pci-proxy`. With no argument the script runs all of them:

```bash
scripts/smoke.sh
```

Name one or more groups to run only those:

```bash
scripts/smoke.sh encrypted-provider
```

The apps must already be running, in the mode that `PCI_PROXY_MODE` names (`off` by default):

```bash
PCI_PROXY_MODE=egress scripts/smoke.sh
```

```bash
PCI_PROXY_MODE=on scripts/smoke.sh
```

The script exits non-zero if any check fails. Every adapter check passes in all three modes. The
`pci-proxy` group calls the PCI Proxy directly: a marker in a body and in a query string, the profiles
(the encrypted provider answers `200` only to a properly encrypted body, so it is the oracle for the
`encrypt` hook, and only for that: it accepts the marker as an account number), and every error code that
needs nothing but the running stack. No row proves that a marker was replaced, or that the hook runs after
the replacement (the plain provider answers `200` to any body and query): that is shown by one-off raw
captures of what the provider receives, not by the script. The `forwarder` group
checks the Forwarder itself and then, from the Forwarder's log (`podman logs`), which provider calls went
through it: all of them in `egress`, none in `off`, and in `on` only the ones without a marker. That log is
the running container's, so restart `run-all.sh` (it starts a fresh container) when you switch the mode.
If the Forwarder is not reachable the group prints `SKIP forwarder` and the other groups run as usual.

While the script runs, the EncryptedProvider's log may gain a Kestrel `fail:` line, `Unexpected exception
in TimingPipeFlusher.FlushAsync ... Bad address` (and an `Unhandled exception while processing` line
after it). It is harmless noise from the script's own `curl` calls to the encrypted provider, usually the
`forwarder` group's call through the Forwarder's tunnel: the stack trace is an HTTP/2 GOAWAY written to a
TLS connection that curl has already closed, and no check fails because of it. The adapters' calls and the
PCI Proxy's calls to the encrypted provider were not seen to produce it (the line is intermittent). It is there with the stage 2 code in `egress` mode too, where no PCI Proxy is involved.

## Repointing an adapter

Each adapter reads its downstream URLs from configuration (`Tokenizer:BaseUrl` and `Provider:BaseUrl`;
a path in the URL is kept), so an adapter can be repointed without code changes. With the sandbox
running, start a second copy of an adapter on another port. Here the PlainAdapter reaches the plain
provider by its loopback address and the EncryptedAdapter reaches the encrypted provider as `localhost`,
another of the certificate's names. Each `dotnet run` stays in the foreground, so give it its own terminal:

```bash
# terminal 2
Urls=http://127.0.0.1:5401 Provider__BaseUrl=http://127.0.0.1:5200 dotnet run --project src/PlainAdapter
```

```bash
# terminal 3
Urls=http://127.0.0.1:5501 Provider__BaseUrl=https://localhost:5300 dotnet run --project src/EncryptedAdapter
```

Then, in a fourth terminal:

```bash
curl -i http://127.0.0.1:5401/balance
curl -i http://127.0.0.1:5501/balance
```

`Provider__BaseUrl` is the provider's own address, not the PCI Proxy's: the proxy takes the provider's
URL in `X-Proxy-Url`, so an adapter reaches it with `PciProxy__Mode=on` (and `PciProxy__ProxyUrl` if the
proxy is elsewhere), not by repointing `Provider__BaseUrl`. Two things to keep in mind:

- The EncryptedAdapter accepts an HTTPS provider's certificate only if it is the one named by
  `Provider:TrustedCertificate` (or chains to it) and the host name in the URL is one the certificate
  carries. The default in its `appsettings.json` is `../../certs/sandbox.crt`, relative to the adapter's
  project folder. A provider with its own certificate needs
  `Provider__TrustedCertificate=<path to that certificate>`. The PlainAdapter has no
  `Provider:TrustedCertificate`, so for an HTTPS provider it uses .NET's default trust, which rejects
  the sandbox's self-signed certificate; it honours `Provider__TrustedCertificate` if you set it.
- With `PciProxy__Mode=egress` on the copy, the provider host has to be one the Forwarder knows,
  `plain-provider.localhost` or `encrypted-provider.localhost`. With `127.0.0.1` (or `localhost`) the
  Forwarder looks in its own container and the call fails: the Forwarder answers `503`, which the
  PlainAdapter passes on and the EncryptedAdapter turns into a `500`. The same holds with
  `PciProxy__Mode=on` for the requests that go through the Forwarder, the ones without a marker (balance):
  the PlainAdapter copy answers `503` and the EncryptedAdapter copy `500`, while create and validate work,
  because the PCI Proxy calls the provider directly from the Mac.

Or edit `Provider:BaseUrl` in the adapter's `appsettings.json` and restart `run-all.sh`.

## Layout

`src/Sandbox.Shared` holds the request and outbound logging, the DTOs, the cipher, the PCI marker, the
provider client setup (`PciProxy:Mode`, the certificate trust) and the adapters' `PciProxyHandler`, which
re-sends a request that carries a marker to the PCI Proxy. The cipher is AES-CBC with a fixed dummy key and
IV. It exists only to give the encrypted provider something to reject, and must never be reused.

`src/PciProxy` is the PCI Proxy: `SendPipeline.cs` (the endpoint), `MarkerReplacer.cs` (marker search and
replacement on bytes), `TokenizerClient.cs` (the tokenizer calls and their retries), `ProviderProfiles.cs`
(the profiles and the `encrypt` hook) and `ProxyException.cs` (the errors).

`forwarder/envoy.yaml` is the Forwarder's Envoy configuration; `scripts/` holds `run-all.sh`,
`smoke.sh`, `gen-cert.sh` and `forwarder.sh`.

The design is in `docs/superpowers/specs/` and the implementation plans in `docs/superpowers/plans/`.

## Will Call: the PCI Proxy as the WillCall Gateway

The PDR's WillCall Gateway is a YARP route of the PCI Proxy (`Yarp.ReverseProxy` 2.3.0, configured in `ReverseProxy` in
`src/PciProxy/appsettings.json`): everything under `/will-call/` goes to the WillCallAdapter, with the prefix removed.

```
client ─► PciProxy :5700 /will-call/** ─► WillCallAdapter :5800
                    └─► Tokenizer :5100   (one call per distinct token, to replace the markers in the response)
```

```bash
curl -s http://127.0.0.1:5700/will-call/lookup-transactions   # two transactions with raw account numbers, in either mode
curl -s http://127.0.0.1:5800/lookup-transactions             # the adapter alone: raw in off, markers in on
open http://127.0.0.1:5700/will-call/swagger                  # the adapter's Swagger UI, through the proxy
```

Rules of the route: every 2xx JSON response has the markers in its string values replaced with raw values (any other
response streams through; a JSON body without a marker is returned byte for byte); a token the tokenizer rejects, a
tokenizer that is down or a malformed marker makes the whole response `500` with `X-Proxy-Code` (`TOKEN_INVALID`,
`TOKENIZER_UNAVAILABLE`, `MARKER_SYNTAX`); a request with marker syntax in its path, query, headers or body is refused with `400
MARKER_SYNTAX`. The proxy asks for uncompressed responses and sends `X-Forwarded-Prefix: /will-call`, which the adapter's Swagger
document uses as its server URL. Not done, left from the PDR: the list routes that drop unresolved items, authentication, a body
size limit. `scripts/smoke.sh will-call` checks it all (`PCI_PROXY_MODE` is the adapter's mode).

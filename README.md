# pdr-pi-pci-proxy

A local sandbox for the PDR "Adapter PCI Proxy". It reproduces **today's** flow from Figure 1: an
adapter detokenizes a bank account through the tokenizer and sends the **raw** account number to a
provider. It also has the PDR's **Forwarder** (an Envoy proxy): with `PciProxy:Mode=egress` the
adapters send their provider calls through it. The PCI Proxy and the WillCall Gateway are not part of
it; they come later.

You call the adapters with curl and watch each app's console to see how a request travels.

| App | Port | Role |
|---|---|---|
| Tokenizer | 5100 | `POST /detokenize` takes `{"token":"..."}` and always answers `{"value":"012345678901234567"}`, the raw account number |
| PlainProvider | 5200 | `POST /create-transaction` logs whatever it receives; `GET /validate-transaction?bankAccountNumber=...` always answers `true` (the raw account number travels in the query string); `GET /balance` |
| EncryptedProvider | 5300 | **HTTPS only**, with a self-signed certificate. `POST /create-transaction` and `POST /validate-transaction` expect `{"payload":"<base64>"}`; they answer `400` if the payload is not a properly encrypted transaction (create) or `{"bankAccountNumber":"..."}` (validate); validate answers `true`; `GET /balance` |
| PlainAdapter | 5400 | `POST /create-transaction`, `POST /validate-transaction` and `GET /balance`, calls PlainProvider at `http://plain-provider.localhost:5200` |
| EncryptedAdapter | 5500 | `POST /create-transaction`, `POST /validate-transaction` and `GET /balance`, calls EncryptedProvider at `https://encrypted-provider.localhost:5300` |
| Forwarder | 5600, 5601 | Envoy 1.36.9 in the Podman container `sandbox-forwarder`: a forward proxy that adds no headers and has no logic. 5600 is the proxy, 5601 its admin interface (`curl http://127.0.0.1:5601/ready`). The adapters use it only in `egress` mode |

Every endpoint is anonymous. Requires the .NET 10 SDK and `openssl` (it creates the certificate).
The Forwarder, and so the `egress` mode, also needs Podman with its machine running (the image
`docker.io/envoyproxy/envoy:v1.36.9` is fetched by Podman if it is not in local storage yet). In `off`
mode the Forwarder is optional: without Podman the rest of the sandbox works.

## Run

```bash
scripts/run-all.sh
```

This creates the sandbox certificate if it is missing, builds once, starts the Forwarder container
and starts all five apps; each log line is prefixed with the app name, `[Forwarder]` for the
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

### HTTPS and names

`scripts/gen-cert.sh` creates `certs/sandbox.crt` and `certs/sandbox.key` once (a self-signed certificate
for `encrypted-provider.localhost`, `localhost` and `127.0.0.1`). `certs/` is git-ignored and nothing is
added to any system or keychain trust store. The EncryptedProvider serves it, and the EncryptedAdapter
trusts exactly that file (`Provider:TrustedCertificate`) and checks the host name; any other
certificate is rejected. To call the provider yourself:

```bash
curl --cacert certs/sandbox.crt https://127.0.0.1:5300/balance
```

The adapters reach the providers by `plain-provider.localhost` and `encrypted-provider.localhost`.
macOS resolves every `*.localhost` name to loopback, so direct calls need nothing. The Forwarder
container cannot see the Mac's loopback, so `scripts/forwarder.sh` gives it the same two names, pointing
at the Mac host (the Forwarder and the adapters have to agree on the provider names). The tokenizer
stays at `http://127.0.0.1:5100` and is never called through the Forwarder.

### The Forwarder: `off` and `egress`

```bash
PciProxy__Mode=egress scripts/run-all.sh
```

`PciProxy:Mode` is `off` (the default: an adapter calls its provider directly) or `egress`: every
provider call of the adapter (create, validate and balance) goes through the Forwarder at
`PciProxy:ForwarderUrl` (`http://127.0.0.1:5600`). The plain provider's HTTP requests are forwarded as
they are; the encrypted provider is reached through a `CONNECT` tunnel, so the TLS session still ends at
the provider. Any other mode value stops the adapter at startup with a message. Each adapter logs
`PciProxy mode=...` when it starts. Without a running Forwarder, provider calls in `egress` mode fail.

`scripts/forwarder.sh start|stop|logs` manages the container on its own.

## What to look for in the logs

- **PlainAdapter / EncryptedAdapter:** the call to the tokenizer with the placeholder token
  `Y000000000000001`, the raw account number that comes back, then the call to the provider.
- **PlainProvider:** the request body as received, with the raw `bankAccountNumber`. For validate, the
  raw number shows up in the URL: `GET /validate-transaction?bankAccountNumber=...`.
- **EncryptedProvider:** the decrypted transaction (or `decrypted validation request`), or
  `rejected: <reason>` for a request it could not decrypt. The EncryptedAdapter also logs the
  `plaintext` it encrypted.
- **Forwarder:** one access-log line per request (two for a `CONNECT`, see below), in UTC, as
  `[time] METHOD host:port/path -> upstream-address status ua=User-Agent`. In `egress` mode:
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
    such as the smoke checks' `ua=curl/...` ones).

  A single Envoy warning about a connection limit at startup is expected.

## Checks

```bash
scripts/smoke.sh                           # every group
scripts/smoke.sh encrypted-provider        # one group
PCI_PROXY_MODE=egress scripts/smoke.sh     # the apps were started with PciProxy__Mode=egress
```

The apps must already be running. The script exits non-zero if any check fails. Every adapter check
passes in both modes. The `forwarder` group checks the Forwarder itself and then, from the Forwarder's
log (`podman logs`), that the adapters' provider calls went through it (`egress`) or did not (`off`).
`PCI_PROXY_MODE` (`off` by default) must match the mode the apps run in. That log is the running
container's, so restart `run-all.sh` (it starts a fresh container) when you switch the mode. If the
Forwarder is not reachable the group prints `SKIP forwarder` and the other groups run as usual.

## Later: putting the PCI Proxy in front

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

Point `Provider__BaseUrl` at the PCI Proxy instead once there is one. Two things to keep in mind:

- The EncryptedAdapter accepts an HTTPS provider's certificate only if it is the one named by
  `Provider:TrustedCertificate` (or chains to it) and the host name in the URL is one the certificate
  carries. The default in its `appsettings.json` is `../../certs/sandbox.crt`, relative to the adapter's
  project folder. A PCI Proxy with its own certificate needs
  `Provider__TrustedCertificate=<path to that certificate>`. The PlainAdapter has no
  `Provider:TrustedCertificate`, so for an HTTPS provider it uses .NET's default trust, which rejects
  the sandbox's self-signed certificate; it honours `Provider__TrustedCertificate` if you set it.
- With `PciProxy__Mode=egress` on the copy, the provider host has to be one the Forwarder knows,
  `plain-provider.localhost` or `encrypted-provider.localhost`. With `127.0.0.1` (or `localhost`) the
  Forwarder looks in its own container and the call fails: the Forwarder answers `503`, which the
  PlainAdapter passes on and the EncryptedAdapter turns into a `500`.

Or edit `Provider:BaseUrl` in the adapter's `appsettings.json` and restart `run-all.sh`.

## Layout

`src/Sandbox.Shared` holds the request and outbound logging, the DTOs, the cipher and the provider
client setup (`PciProxy:Mode`, the certificate trust). The cipher is AES-CBC with a fixed dummy key and
IV. It exists only to give the encrypted provider something to reject, and must never be reused.

`forwarder/envoy.yaml` is the Forwarder's Envoy configuration; `scripts/` holds `run-all.sh`,
`smoke.sh`, `gen-cert.sh` and `forwarder.sh`.

The design is in `docs/superpowers/specs/` and the implementation plans in `docs/superpowers/plans/`.

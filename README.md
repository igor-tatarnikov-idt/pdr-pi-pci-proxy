# pdr-pi-pci-proxy

A local sandbox for the PDR "Adapter PCI Proxy". It reproduces **today's** flow from Figure 1: an
adapter detokenizes a bank account through the tokenizer and sends the **raw** account number to a
provider. The PCI Proxy, the Forwarder and the WillCall Gateway are not part of it; they come later.

You call the adapters with curl and watch each app's console to see how a request travels.

| App | Port | Role |
|---|---|---|
| Tokenizer | 5100 | `POST /detokenize` takes `{"token":"..."}` and always answers `{"value":"012345678901234567"}`, the raw account number |
| PlainProvider | 5200 | `POST /create-transaction` logs whatever it receives; `GET /balance` |
| EncryptedProvider | 5300 | `POST /create-transaction` expects `{"payload":"<base64>"}`; answers `400` if the payload is not a properly encrypted transaction; `GET /balance` |
| PlainAdapter | 5400 | `POST /create-transaction` and `GET /balance`, calls PlainProvider |
| EncryptedAdapter | 5500 | `POST /create-transaction` and `GET /balance`, calls EncryptedProvider |

Every endpoint is anonymous. Requires the .NET 10 SDK.

## Run

```bash
scripts/run-all.sh
```

This builds once and starts all five apps; each log line is prefixed with the app name. Ctrl+C stops
everything. In another terminal:

```bash
curl -i -X POST http://127.0.0.1:5400/create-transaction
curl -i http://127.0.0.1:5400/balance
curl -i -X POST http://127.0.0.1:5500/create-transaction
curl -i http://127.0.0.1:5500/balance
```

The create endpoints take no body: each adapter sends a hard-coded transaction (`T-001-plain` from the
plain adapter, `T-002-encrypted` from the encrypted one).

## What to look for in the logs

- **PlainAdapter / EncryptedAdapter:** the call to the tokenizer with the placeholder token
  `Y000000000000001`, the raw account number that comes back, then the call to the provider.
- **PlainProvider:** the request body as received, with the raw `bankAccountNumber`.
- **EncryptedProvider:** the decrypted transaction, or `rejected: <reason>` for a request it could not
  decrypt. The EncryptedAdapter also logs the `plaintext` it encrypted.

## Checks

```bash
scripts/smoke.sh                 # every group
scripts/smoke.sh encrypted-provider   # one group
```

The apps must already be running. The script exits non-zero if any check fails.

## Later: putting the PCI Proxy in front

Each adapter reads its downstream URLs from configuration (`Tokenizer:BaseUrl` and `Provider:BaseUrl`;
a path in the URL is kept), so an adapter can be repointed without code changes. With the sandbox
running, start a second copy of an adapter on another port:

```bash
Urls=http://127.0.0.1:5401 Provider__BaseUrl=http://127.0.0.1:6000 dotnet run --project src/PlainAdapter
```

Or edit `Provider:BaseUrl` in the adapter's `appsettings.json` and restart `run-all.sh`.

## Layout

`src/Sandbox.Shared` holds the request and outbound logging, the DTOs and the cipher. The cipher is
AES-CBC with a fixed dummy key and IV. It exists only to give the encrypted provider something to
reject, and must never be reused.

The design is in `docs/superpowers/specs/` and the implementation plan in `docs/superpowers/plans/`.

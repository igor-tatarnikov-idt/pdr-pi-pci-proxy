# Stage 2: Forwarder, PciProxy:Mode and HTTPS Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put the encrypted provider on HTTPS, add an Envoy forwarder in Podman, and give both adapters a `PciProxy:Mode` (`off` or `egress`) that sends their provider traffic through it.

**Architecture:** The encrypted provider serves HTTPS with a generated self-signed certificate that the adapters trust by explicit path. Envoy 1.36.9 (dynamic forward proxy) runs as a Podman container and handles absolute-form HTTP and `CONNECT`. One shared helper configures the adapters' provider `HttpClient` (certificate trust, optional proxy), so the adapters carry no copies of the wiring.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, Kestrel PEM certificates, Envoy 1.36.9, Podman 5.5 (applehv machine), openssl (system LibreSSL works), bash 3.2, curl, python3 (for one-off captures).

**Spec:** `docs/superpowers/specs/2026-10-16-pci-proxy-sandbox-design.md`, section "Stage 2". The earlier stages' plans stay valid; this plan only adds to them.

## Global Constraints

- Work on branch `stage-2-forwarder-egress`. Do not commit: the author commits when satisfied.
- Everything from stages 0 and 1 still holds: .NET 10, classic `.sln`, ports 5100–5500, all endpoints anonymous, hard-coded payloads, verification by curl smoke checks, bash 3.2 compatible scripts.
- EncryptedProvider serves **HTTPS only** on `https://127.0.0.1:5300`, certificate from the PEM files `certs/sandbox.crt` and `certs/sandbox.key` via Kestrel's `Endpoints` configuration (`Urls` dropped for this app). PlainProvider stays plain HTTP on `:5200`.
- `scripts/gen-cert.sh` makes a self-signed certificate with openssl, subject alternative names `encrypted-provider.localhost`, `localhost`, `127.0.0.1`, and does nothing if the files exist. `certs/` is git-ignored. Nothing is installed into any system or keychain trust store.
- Adapters trust the certificate through `Provider:TrustedCertificate` (path to `certs/sandbox.crt`): it is a custom root for the provider client, the host name is still verified, any other certificate is rejected.
- Provider URLs in the adapters: `http://plain-provider.localhost:5200` and `https://encrypted-provider.localhost:5300`. The tokenizer stays `http://127.0.0.1:5100` and its client never uses the proxy.
- Forwarder: image `docker.io/envoyproxy/envoy:v1.36.9` (already in local Podman storage), config `forwarder/envoy.yaml`, container `sandbox-forwarder`, proxy published on `127.0.0.1:5600`, admin on `127.0.0.1:5601` (container port 9901). `--add-host plain-provider.localhost:<H>` and `--add-host encrypted-provider.localhost:<H>` where `<H>` is what `host.containers.internal` resolves to inside the container (`192.168.127.254` here); `host-gateway` does not work in this Podman setup. No headers added to the provider request or response. Access log to stdout.
- `PciProxy:Mode` is `off` (default) or `egress`; `PciProxy:ForwarderUrl` is `http://127.0.0.1:5600`. Any other value stops the adapter at startup with a clear message. In `egress` every provider call (create, validate, balance) goes through the Forwarder. Switching: `PciProxy__Mode=egress scripts/run-all.sh`.
- `run-all.sh` starts the Forwarder as `[Forwarder]`, removes a stale container first, stops it on exit, and only warns (then continues) if `podman` or its machine is unavailable.
- Every existing adapter check in `scripts/smoke.sh` passes unchanged in both modes.

## Findings from the pre-plan spike (verified on this machine)

- The Envoy image's stock entrypoint fails under this Podman setup (`chown: changing ownership of '/dev/stdout': Permission denied`). Run the binary directly: `--entrypoint envoy` with `-c /etc/envoy/envoy.yaml --log-level warn`.
- `<H>` comes from `podman run --rm --entrypoint getent <image> hosts host.containers.internal` (first field). The Mac cannot resolve `host.containers.internal`, but resolves any `*.localhost` name to loopback.
- Envoy injects `x-forwarded-proto` unless the virtual host removes it (done in the config below). It lower-cases header names (harmless in HTTP/1.1) and adds `date` only when the upstream omitted it.
- The system `/usr/bin/openssl` is LibreSSL 3.3.6: it supports the config-file recipe below but not `x509 -ext`.
- Mounting a file under `/Users` into the container works (virtiofs).

## Review Focus

1. The adapter must reject a provider certificate other than the sandbox one. Pinned by Task 1 step 6.
2. The adapter must still verify the host name: a name not in the certificate's SANs (for example `other.localhost`) must fail even though the certificate is trusted. Pinned by Task 1 step 7.
3. An invalid `PciProxy:Mode` (`shadow`, `on`, a typo, empty) must stop the adapter with a message and exit code 1, never fall back to `off`. Pinned by Task 3 step 5.
4. In `egress` with the Forwarder down, a provider call must fail, not silently go direct. Pinned by Task 3 step 6.
5. Stopping `run-all.sh` must remove the `sandbox-forwarder` container and free ports 5600/5601; a missing `podman` must only warn. Pinned by Task 4 steps 3–4.

## File Structure

| Path | Responsibility |
|---|---|
| `scripts/gen-cert.sh` | Generate `certs/sandbox.{crt,key}` once |
| `src/Sandbox.Shared/ProviderClientExtensions.cs` | `PciProxyMode`, mode parsing, and the provider handler (certificate trust, proxy) |
| `forwarder/envoy.yaml` | Envoy config: dynamic forward proxy for HTTP and `CONNECT` |
| `scripts/forwarder.sh` | `start`, `stop`, `logs` for the `sandbox-forwarder` container |
| `src/EncryptedProvider/appsettings.json` | Kestrel HTTPS endpoint |
| `src/*Adapter/Program.cs`, `appsettings.json` | Use the helper; new provider URLs; `PciProxy` section |
| `scripts/smoke.sh`, `scripts/run-all.sh`, `README.md`, spec | Checks, orchestration, docs |

## Working conventions

- Start the stack with `scripts/run-all.sh > $TMPDIR/<log> 2>&1 &`, wait until `curl -s -o /dev/null http://127.0.0.1:5500/balance` answers, and stop it with `kill -TERM <pid>` (a backgrounded non-interactive script ignores SIGINT). Check `lsof -nP -iTCP:5100-5601 -sTCP:LISTEN` is empty afterwards.
- A second copy of an adapter for one-off checks: `Urls=http://127.0.0.1:5501 <Setting>__<Key>=<value> dotnet run --project src/EncryptedAdapter --no-launch-profile --no-build`.
- Run `dotnet build` after each code change; it must report 0 warnings and 0 errors.

---

### Task 1: HTTPS for the EncryptedProvider

**Files:**
- Create: `scripts/gen-cert.sh`, `src/Sandbox.Shared/ProviderClientExtensions.cs`
- Modify: `.gitignore`, `src/EncryptedProvider/appsettings.json`, `src/EncryptedAdapter/Program.cs`, `src/EncryptedAdapter/appsettings.json`, `scripts/smoke.sh`, `scripts/run-all.sh`

**Interfaces:**
- Produces, `Sandbox.Shared`: `static class ProviderClientExtensions { static IHttpClientBuilder ConfigureProviderHandler(this IHttpClientBuilder builder, IConfiguration configuration, string contentRoot) }`. It sets the primary handler to a `SocketsHttpHandler`. If `Provider:TrustedCertificate` is set, the path is resolved against `contentRoot` and the file must exist (otherwise fail at startup with a message that mentions `scripts/gen-cert.sh`); the server certificate validation callback fails on `RemoteCertificateNameMismatch`, and otherwise accepts only if the chain builds with that certificate as the sole custom root (`X509ChainTrustMode.CustomRootTrust`, no revocation check). Task 3 extends this same method with the proxy.
- Produces: `scripts/smoke.sh` now `cd`s to the repo root, sets `CERT=certs/sandbox.crt` and `ENC_PROVIDER=https://127.0.0.1:5300`, and `check` adds `--cacert "$CERT"` to every curl call.

- [ ] **Step 1: Write the failing checks.** In `scripts/smoke.sh`: `cd "$(dirname "$0")/.."` at the top, define `CERT`, change `ENC_PROVIDER` to the HTTPS URL, and add `--cacert "$CERT"` inside `check`. Start the stack on the unchanged code (`scripts/run-all.sh`). Run `scripts/smoke.sh encrypted-provider` → every check `FAIL (got 000 …)` (the provider still speaks HTTP and the certificate does not exist). Stop the stack.
- [ ] **Step 2: Implement `scripts/gen-cert.sh`** (executable). If `certs/sandbox.crt` and `certs/sandbox.key` both exist, print `certs already exist` and exit 0. Otherwise write a temporary openssl config and run `openssl req -x509 -newkey rsa:2048 -nodes -days 825 -keyout certs/sandbox.key -out certs/sandbox.crt -config <tmp>`:
  ```
  [req]
  distinguished_name = dn
  x509_extensions = ext
  prompt = no
  [dn]
  CN = encrypted-provider.localhost
  [ext]
  subjectAltName = DNS:encrypted-provider.localhost, DNS:localhost, IP:127.0.0.1
  extendedKeyUsage = serverAuth
  ```
  Add `certs/` to `.gitignore`. Run the script twice → second run prints `certs already exist`; `git status --short` does not list `certs/`.
- [ ] **Step 3: Switch EncryptedProvider to HTTPS** in its `appsettings.json`: remove `Urls`, add `Kestrel:Endpoints:Https` with `Url` `https://127.0.0.1:5300` and `Certificate` `Path` `../../certs/sandbox.crt`, `KeyPath` `../../certs/sandbox.key` (relative to the content root `src/EncryptedProvider`).
- [ ] **Step 4: Implement `ConfigureProviderHandler`** per the interface. Load the PEM certificate with `X509CertificateLoader`; if the file loader rejects PEM, base64-decode the PEM body and use `LoadCertificate(byte[])`. Apply it to the EncryptedAdapter's "provider" client (`builder.Environment.ContentRootPath` as `contentRoot`), set its `Provider:BaseUrl` to `https://encrypted-provider.localhost:5300` and add `Provider:TrustedCertificate` `../../certs/sandbox.crt` to its `appsettings.json`. In `scripts/run-all.sh` call `scripts/gen-cert.sh` before the build.
- [ ] **Step 5: Run and verify.** Start the stack with `run-all.sh`; `scripts/smoke.sh` → all checks `PASS` (the full suite, including both adapters). `curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5300/balance` → not `200` (plain HTTP to the HTTPS port). Stop the stack.
- [ ] **Step 6: Wrong certificate is rejected (Review Focus 1).** Generate a second self-signed certificate for `encrypted-provider.localhost` in a scratch directory and serve it with `openssl s_server -accept 5399 -cert … -key … -www`. Start the real provider (stack running), then a second EncryptedAdapter on 5501 with `Provider__BaseUrl=https://encrypted-provider.localhost:5399`. `curl -s -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:5501/create-transaction` → `500`, and its log shows a TLS validation failure. Stop the extras.
- [ ] **Step 7: Host-name check still applies (Review Focus 2).** A second EncryptedAdapter on 5501 with `Provider__BaseUrl=https://other.localhost:5300`: the same curl → `500`, with a TLS failure in its log. Stop everything.

### Task 2: The Forwarder (Envoy in Podman)

**Files:**
- Create: `forwarder/envoy.yaml`, `scripts/forwarder.sh`
- Modify: `scripts/smoke.sh`

**Interfaces:**
- Consumes: Task 1's HTTPS provider, `check`, `CERT`.
- Produces: `scripts/forwarder.sh start|stop|logs`. `start` fails with a message if `podman` is missing or its machine is not running; otherwise removes any `sandbox-forwarder` container, resolves `<H>`, starts the container detached (`-p 127.0.0.1:5600:5600 -p 127.0.0.1:5601:9901`, the two `--add-host` entries, `-v <repo>/forwarder/envoy.yaml:/etc/envoy/envoy.yaml:ro`, `--entrypoint envoy`), and waits up to 20 s for `http://127.0.0.1:5601/ready` to return 200 (otherwise prints the container logs and fails). `stop` is `podman rm -f sandbox-forwarder`. `logs` is `podman logs -f sandbox-forwarder`.
- Produces: smoke variables `FORWARDER=http://127.0.0.1:5600`, `FORWARDER_ADMIN=http://127.0.0.1:5601`, and group `forwarder`: if `$FORWARDER_ADMIN/ready` is not reachable it prints `SKIP forwarder (admin not reachable)` and returns without failing.

- [ ] **Step 1: Write the failing checks** in a `forwarder` group (`-x` is curl's proxy flag):
  ```bash
  check "forwarder ready" 200 '' "$FORWARDER_ADMIN/ready"
  check "forwarder: plain provider over HTTP" 200 '"balance":1000' -x "$FORWARDER" "http://plain-provider.localhost:5200/balance"
  check "forwarder: encrypted provider over CONNECT and TLS" 200 '"balance":1000' -x "$FORWARDER" "https://encrypted-provider.localhost:5300/balance"
  ```
  With the stack running and no Forwarder, `scripts/smoke.sh forwarder` → `SKIP forwarder (admin not reachable)`. To see RED, temporarily call the group's checks without the skip guard (or point `FORWARDER_ADMIN` at the real admin before it exists) → the three checks `FAIL (got 000 …)`; restore the guard.
- [ ] **Step 2: Create `forwarder/envoy.yaml`** with exactly this content (validated against Envoy 1.36.9):
  ```yaml
  admin:
    address: { socket_address: { address: 0.0.0.0, port_value: 9901 } }
  static_resources:
    listeners:
    - name: forwarder
      address: { socket_address: { address: 0.0.0.0, port_value: 5600 } }
      filter_chains:
      - filters:
        - name: envoy.filters.network.http_connection_manager
          typed_config:
            "@type": type.googleapis.com/envoy.extensions.filters.network.http_connection_manager.v3.HttpConnectionManager
            stat_prefix: forwarder
            http_protocol_options: { allow_absolute_url: true }
            generate_request_id: false
            skip_xff_append: true
            server_header_transformation: PASS_THROUGH
            upgrade_configs:
            - upgrade_type: CONNECT
            access_log:
            - name: envoy.access_loggers.stdout
              typed_config:
                "@type": type.googleapis.com/envoy.extensions.access_loggers.stream.v3.StdoutAccessLog
                log_format:
                  text_format_source:
                    inline_string: "[%START_TIME%] %REQ(:METHOD)% %REQ(:AUTHORITY)%%REQ(:PATH)% -> %UPSTREAM_HOST% %RESPONSE_CODE%\n"
            route_config:
              name: all
              virtual_hosts:
              - name: all
                domains: ["*"]
                request_headers_to_remove: ["x-forwarded-proto"]
                routes:
                - match: { connect_matcher: {} }
                  route:
                    cluster: forward_proxy
                    upgrade_configs:
                    - upgrade_type: CONNECT
                      connect_config: {}
                - match: { prefix: "/" }
                  route: { cluster: forward_proxy }
            http_filters:
            - name: envoy.filters.http.dynamic_forward_proxy
              typed_config:
                "@type": type.googleapis.com/envoy.extensions.filters.http.dynamic_forward_proxy.v3.FilterConfig
                dns_cache_config: &dns
                  name: forward_proxy_cache
                  dns_lookup_family: V4_ONLY
                  typed_dns_resolver_config:
                    name: envoy.network.dns_resolver.getaddrinfo
                    typed_config:
                      "@type": type.googleapis.com/envoy.extensions.network.dns_resolver.getaddrinfo.v3.GetAddrInfoDnsResolverConfig
            - name: envoy.filters.http.router
              typed_config:
                "@type": type.googleapis.com/envoy.extensions.filters.http.router.v3.Router
                suppress_envoy_headers: true
    clusters:
    - name: forward_proxy
      lb_policy: CLUSTER_PROVIDED
      cluster_type:
        name: envoy.clusters.dynamic_forward_proxy
        typed_config:
          "@type": type.googleapis.com/envoy.extensions.clusters.dynamic_forward_proxy.v3.ClusterConfig
          dns_cache_config: *dns
  ```
- [ ] **Step 3: Implement `scripts/forwarder.sh`** (executable) per the interface.
- [ ] **Step 4: Run and verify.** Start the stack (`run-all.sh`), then `scripts/forwarder.sh start` → exits 0. `scripts/smoke.sh forwarder` → all three `PASS`. `scripts/forwarder.sh logs` (or `podman logs sandbox-forwarder`) shows `GET plain-provider.localhost:5200/balance -> 192.168.127.254:5200 200` and a `CONNECT encrypted-provider.localhost:5300` line.
- [ ] **Step 5: No headers added.** Run a small python server on `127.0.0.1:5892` that saves the raw bytes of one request and answers `200`. `curl -x http://127.0.0.1:5600 -X POST -H 'content-type: application/json' -d '{"a":1}' http://plain-provider.localhost:5892/create-transaction` → the saved request contains exactly `host`, `user-agent`, `accept`, `content-type`, `content-length` and the body: no `x-forwarded-proto`, `x-request-id`, `x-envoy-*`, `via`.
- [ ] **Step 6: Stop** the stack and run `scripts/forwarder.sh stop`; `podman ps -a --filter name=sandbox-forwarder` lists nothing.

### Task 3: `PciProxy:Mode` in the adapters

**Files:**
- Modify: `src/Sandbox.Shared/ProviderClientExtensions.cs`, `src/PlainAdapter/Program.cs`, `src/PlainAdapter/appsettings.json`, `src/EncryptedAdapter/Program.cs`, `src/EncryptedAdapter/appsettings.json`, `scripts/smoke.sh`

**Interfaces:**
- Consumes: `ConfigureProviderHandler(builder, configuration, contentRoot)` from Task 1, `forwarder.sh`, the `forwarder` group.
- Produces, `Sandbox.Shared`: `enum PciProxyMode { Off, Egress }` and `static PciProxyMode GetPciProxyMode(this IConfiguration configuration)`. It reads `PciProxy:Mode` case-insensitively (`off`, `egress`); anything else, including empty, `shadow` and `on`, prints `PciProxy:Mode must be 'off' or 'egress' (got '<value>')` to stderr and exits with code 1. `ConfigureProviderHandler` additionally sets `Proxy = new WebProxy(<PciProxy:ForwarderUrl>)` and `UseProxy = true` on the `SocketsHttpHandler` when the mode is `Egress`. The tokenizer client keeps its default handler.
- Produces: each adapter calls `GetPciProxyMode()` before building the app and logs `PciProxy mode=<off|egress> forwarder=<url>` (the URL only for `egress`) at startup. Both `appsettings.json` files get `"PciProxy": { "Mode": "off", "ForwarderUrl": "http://127.0.0.1:5600" }` and the new provider URLs from Global Constraints.
- Produces: `scripts/smoke.sh` helper `check_log <name> <present|absent> <grep -E pattern> <text>`, and `PCI_PROXY_MODE` (default `off`) read by the `forwarder` group.

- [ ] **Step 1: Write the failing checks.** Extend the `forwarder` group (after its guard): remember `before=$(podman logs sandbox-forwarder 2>&1 | wc -l)`; call, in this order, `POST $PLAIN_ADAPTER/create-transaction`, `POST $PLAIN_ADAPTER/validate-transaction`, `GET $PLAIN_ADAPTER/balance`, `POST $ENC_ADAPTER/create-transaction`; take the new log lines (`tail -n +$((before+1))`) as `new` and the whole log as `all`. For `PCI_PROXY_MODE=egress`:
  - `check_log … present 'POST plain-provider\.localhost:5200/create-transaction'` on `new`
  - `check_log … present 'GET plain-provider\.localhost:5200/validate-transaction\?bankAccountNumber=012345678901234567'` on `new`
  - `check_log … present 'GET plain-provider\.localhost:5200/balance'` on `new`
  - `check_log … present 'CONNECT encrypted-provider\.localhost:5300'` on `all` (a pooled tunnel may be reused, so the whole log is searched)
  - `check_log … absent 'detokenize|:5100'` on `all` (the tokenizer never goes through the Forwarder)

  For `off`: the same patterns for `plain-provider.localhost` and `CONNECT encrypted-provider` must be `absent` on `new` and `all`. Start the Forwarder and the stack with `PciProxy__Mode=egress scripts/run-all.sh` on the unchanged code; `PCI_PROXY_MODE=egress scripts/smoke.sh forwarder` → the egress `present` checks `FAIL` (the old adapters go direct).
- [ ] **Step 2: Implement the mode** in `ProviderClientExtensions.cs` per the interface, and apply `ConfigureProviderHandler` and the startup log to the PlainAdapter (including its provider client, which has no `Provider:TrustedCertificate`) and the EncryptedAdapter.
- [ ] **Step 3: Switch both adapters' provider URLs** to `http://plain-provider.localhost:5200` and `https://encrypted-provider.localhost:5300` and add the `PciProxy` section to both `appsettings.json` files.
- [ ] **Step 4: Run and verify both modes.**
  - `off`: `scripts/forwarder.sh start`, `scripts/run-all.sh`, `scripts/smoke.sh` → every check `PASS` including the forwarder group's `absent` checks. Stop the stack.
  - `egress`: `PciProxy__Mode=egress scripts/run-all.sh`, `PCI_PROXY_MODE=egress scripts/smoke.sh` → every check `PASS`. The adapter logs show `PciProxy mode=egress forwarder=http://127.0.0.1:5600`, and the Forwarder log shows the plain adapter's create, validate and balance requests and a `CONNECT` for the encrypted provider. Stop the stack.
- [ ] **Step 5: Invalid modes (Review Focus 3).** For each of `shadow`, `on`, `banana` and an empty string, run `PciProxy__Mode=<value> dotnet run --project src/PlainAdapter --no-launch-profile --no-build` (and the EncryptedAdapter) → the message on stderr, exit code 1, nothing listening on the adapter's port. `EGRESS` is accepted.
- [ ] **Step 6: No silent fallback (Review Focus 4).** Stop the Forwarder only (`scripts/forwarder.sh stop`) while the stack runs in `egress`. `curl -s -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:5400/create-transaction` → not `200`, and the PlainProvider log gains no new line. Same for port 5500 and the EncryptedProvider. Stop everything.

### Task 4: run-all integration, README, spec bookkeeping

**Files:**
- Modify: `scripts/run-all.sh`, `README.md`, `docs/superpowers/specs/2026-10-16-pci-proxy-sandbox-design.md`

**Interfaces:**
- Consumes: `scripts/forwarder.sh`, `scripts/gen-cert.sh`.

- [ ] **Step 1: Integrate the Forwarder into `scripts/run-all.sh`.** After the build and `gen-cert.sh`, try `scripts/forwarder.sh start`. On success, stream `scripts/forwarder.sh logs` through the same FIFO and `awk` prefixer as the apps with the prefix `[Forwarder] ` (and record its PID like the others). On failure, print `warning: Forwarder not started (<reason>); egress mode will not work` and carry on. In `cleanup`, run `scripts/forwarder.sh stop` after killing the apps and before the final `wait`.
- [ ] **Step 2: Update `README.md`.** Add the Forwarder to the port table (5600 proxy, 5601 admin), the HTTPS provider and `scripts/gen-cert.sh`/`certs/`, the `*.localhost` names, `PciProxy__Mode=egress scripts/run-all.sh` with `PCI_PROXY_MODE=egress scripts/smoke.sh`, the Podman requirement for `egress` (the Forwarder is optional in `off` mode), what to look for in the `[Forwarder]` log, and fix the "Later" section's second-adapter example so it still works (new provider URLs, HTTPS trust). In the spec's "Confirmed decisions" add: 6. Envoy runs as a Podman container (the image is already local); 7. both HTTP (PlainProvider) and HTTPS (EncryptedProvider) are covered.
- [ ] **Step 3: Verify the whole sandbox in both modes.** `scripts/run-all.sh` (off) → `[Forwarder]` lines appear alongside the app prefixes; `scripts/smoke.sh` → all `PASS`; `kill -TERM` → `podman ps -a --filter name=sandbox-forwarder` lists nothing and `lsof -nP -iTCP:5100-5601 -sTCP:LISTEN` prints nothing (Review Focus 5). Repeat with `PciProxy__Mode=egress` and `PCI_PROXY_MODE=egress`. Then a real-terminal Ctrl-C through a pty (`expect`, as for the earlier stop-check) leaves the same clean state.
- [ ] **Step 4: Podman missing only warns (Review Focus 5).** Run `scripts/run-all.sh` with `PATH` set to only the dotnet and system directories (no `/opt/homebrew/bin`): the warning line appears, the five apps start, `scripts/smoke.sh` passes with the `forwarder` group printing `SKIP`. Stop it.
- [ ] **Step 5: Final state.** `git status --short` lists only intended files (no `certs/`, `bin/`, `obj/`); nothing is committed; the branch is `stage-2-forwarder-egress`.

#!/usr/bin/env bash
# Curl smoke checks for the PCI Proxy sandbox. The apps must already be running.
# Usage: scripts/smoke.sh [group ...]   (no argument runs every group)

cd "$(dirname "$0")/.." || exit 1

CERT=certs/sandbox.crt
TOKENIZER=http://127.0.0.1:5100
PLAIN_PROVIDER=http://127.0.0.1:5200
ENC_PROVIDER=https://127.0.0.1:5300
PLAIN_ADAPTER=http://127.0.0.1:5400
ENC_ADAPTER=http://127.0.0.1:5500
FORWARDER=http://127.0.0.1:5600
FORWARDER_ADMIN=http://127.0.0.1:5601
PCI_PROXY=${PCI_PROXY:-http://127.0.0.1:5700}
WILL_CALL_ADAPTER=http://127.0.0.1:5800
SEND=$PCI_PROXY/api/t/sandbox/providers
# The mode the adapters were started in (PciProxy__Mode); the forwarder group expects the Forwarder log to match it.
PCI_PROXY_MODE=${PCI_PROXY_MODE:-off}
SAMPLE_TX='{"transactionId":"T-000","amount":1.00,"currency":"USD","senderName":"A","recipientName":"B","bankAccountNumber":"0123"}'

# Independent encryptor (openssl) matching the sandbox's dummy AES-CBC key and IV; it also
# cross-checks PayloadCipher.
KEY_HEX=$(printf 'sandbox-dummy-key-do-not-reuse!!' | xxd -p -c 64)
IV_HEX=$(printf 'sandbox-iv-00000' | xxd -p -c 64)
encrypt()  { printf '%s' "$1" | openssl enc -aes-256-cbc -K "$KEY_HEX" -iv "$IV_HEX" -a -A; }
envelope() { printf '{"payload":"%s"}' "$(encrypt "$1")"; }

FAILED=0

# check <name> <expected-status> <expected-body-substring-or-empty> <curl args...>
check() {
  local name=$1 expected=$2 substring=$3 out status body
  shift 3
  out=$(curl -s --cacert "$CERT" -w $'\n%{http_code}' "$@")
  status=${out##*$'\n'}
  body=${out%$'\n'*}
  if [[ $status != "$expected" ]]; then
    echo "FAIL $name (got $status, expected $expected)"
    FAILED=1
  elif [[ -n $substring && $body != *"$substring"* ]]; then
    echo "FAIL $name (body does not contain $substring)"
    FAILED=1
  else
    echo "PASS $name"
  fi
}

# check_proxy <name> <expected-status> <expected-outcome> <expected-code-or-empty> <body-substring-or-empty> <curl args...>
# Like check, and also matches the PCI Proxy's X-Proxy-Outcome and, if given, X-Proxy-Code response headers.
check_proxy() {
  local name=$1 expected=$2 outcome=$3 code=$4 substring=$5 headers body status received
  shift 5
  headers=$(mktemp) && body=$(mktemp) || return 1
  status=$(curl -s -D "$headers" -o "$body" -w '%{http_code}' "$@")
  received=$(tr -d '\r' < "$headers") # curl writes the header lines with CRLF; grep -x needs them without
  if [[ $status != "$expected" ]]; then
    echo "FAIL $name (got $status, expected $expected)"
    FAILED=1
  elif ! grep -qixF -- "x-proxy-outcome: $outcome" <<< "$received"; then
    echo "FAIL $name (X-Proxy-Outcome is not $outcome)"
    FAILED=1
  elif [[ -n $code ]] && ! grep -qixF -- "x-proxy-code: $code" <<< "$received"; then
    echo "FAIL $name (X-Proxy-Code is not $code)"
    FAILED=1
  elif [[ -n $substring ]] && ! grep -qF -- "$substring" "$body"; then
    echo "FAIL $name (body does not contain $substring)"
    FAILED=1
  else
    echo "PASS $name"
  fi
  rm -f "$headers" "$body"
}

# check_log <name> <present|absent> <grep -E pattern> <text>
check_log() {
  local name=$1 expectation=$2 pattern=$3 text=$4 found=absent
  if grep -Eq -- "$pattern" <<< "$text"; then
    found=present
  fi
  if [[ $found == "$expectation" ]]; then
    echo "PASS $name"
  else
    echo "FAIL $name (expected '$pattern' to be $expectation in the Forwarder log)"
    FAILED=1
  fi
}

group_tokenizer() {
  check "tokenizer detokenizes to the constant" 200 '"value":"012345678901234567"' \
    -X POST "$TOKENIZER/detokenize" -H 'content-type: application/json' -d '{"token":"anything"}'
  check "tokenizer: invalid token" 404 'token not found' \
    -X POST "$TOKENIZER/detokenize" -H 'content-type: application/json' -d '{"token":"Y000000000000000"}'
}

group_plain_provider() {
  check "plain provider accepts transaction" 200 '"status":"accepted"' \
    -X POST "$PLAIN_PROVIDER/create-transaction" -H 'content-type: application/json' -d "$SAMPLE_TX"
  check "plain provider balance" 200 '"balance":1000' "$PLAIN_PROVIDER/balance"
  check "plain provider validate (account in query string)" 200 'true' \
    "$PLAIN_PROVIDER/validate-transaction?bankAccountNumber=012345678901234567"
}

group_encrypted_provider() {
  local url="$ENC_PROVIDER/create-transaction" h='content-type: application/json'
  local invalid='"title":"Invalid request"'
  local missing_sender='{"transactionId":"T-000","amount":1.00,"currency":"USD","recipientName":"B","bankAccountNumber":"0123"}'
  local null_recipient='{"transactionId":"T-000","amount":1.00,"currency":"USD","senderName":"A","recipientName":null,"bankAccountNumber":"0123"}'
  local missing_amount='{"transactionId":"T-000","currency":"USD","senderName":"A","recipientName":"B","bankAccountNumber":"0123"}'
  check "valid encrypted transaction" 200 '"status":"accepted"' -X POST "$url" -H "$h" -d "$(envelope "$SAMPLE_TX")"
  check "payload not base64" 400 "$invalid" -X POST "$url" -H "$h" -d '{"payload":"not-valid"}'
  check "payload base64 but not ciphertext" 400 "$invalid" -X POST "$url" -H "$h" -d '{"payload":"AAAAAAAAAAAAAAAAAAAAAA=="}'
  check "payload base64 with wrong length" 400 "$invalid" -X POST "$url" -H "$h" -d '{"payload":"AAAA"}'
  check "plain unencrypted transaction" 400 "$invalid" -X POST "$url" -H "$h" -d "$SAMPLE_TX"
  check "empty body" 400 "$invalid" -X POST "$url" -H "$h"
  check "body not JSON" 400 "$invalid" -X POST "$url" -H "$h" -d 'not json'
  check "payload is a number" 400 "$invalid" -X POST "$url" -H "$h" -d '{"payload":123}'
  check "payload is null" 400 "$invalid" -X POST "$url" -H "$h" -d '{"payload":null}'
  check "decrypts to non-JSON" 400 "$invalid" -X POST "$url" -H "$h" -d "$(envelope 'hello')"
  check "decrypts to JSON missing senderName" 400 "$invalid" -X POST "$url" -H "$h" -d "$(envelope "$missing_sender")"
  check "decrypts to JSON with null recipientName" 400 "$invalid" -X POST "$url" -H "$h" -d "$(envelope "$null_recipient")"
  check "decrypts to JSON missing amount" 400 "$invalid" -X POST "$url" -H "$h" -d "$(envelope "$missing_amount")"
  check "encrypted provider balance" 200 '"balance":1000' "$ENC_PROVIDER/balance"
  local vurl="$ENC_PROVIDER/validate-transaction" account='{"bankAccountNumber":"0123"}'
  check "validate: valid encrypted request" 200 'true' -X POST "$vurl" -H "$h" -d "$(envelope "$account")"
  check "validate: payload not base64" 400 "$invalid" -X POST "$vurl" -H "$h" -d '{"payload":"not-valid"}'
  check "validate: plain unencrypted request" 400 "$invalid" -X POST "$vurl" -H "$h" -d "$account"
  check "validate: empty body" 400 "$invalid" -X POST "$vurl" -H "$h"
  check "validate: decrypts to JSON missing bankAccountNumber" 400 "$invalid" -X POST "$vurl" -H "$h" -d "$(envelope '{"foo":"bar"}')"
  check "validate: decrypts to JSON with null bankAccountNumber" 400 "$invalid" -X POST "$vurl" -H "$h" -d "$(envelope '{"bankAccountNumber":null}')"
}

group_plain_adapter() {
  check "plain adapter create" 200 '"status":"accepted"' -X POST "$PLAIN_ADAPTER/create-transaction"
  check "plain adapter balance" 200 '"balance":1000' "$PLAIN_ADAPTER/balance"
  check "plain adapter validate" 200 'true' -X POST "$PLAIN_ADAPTER/validate-transaction"
}

group_encrypted_adapter() {
  check "encrypted adapter create" 200 '"status":"accepted"' -X POST "$ENC_ADAPTER/create-transaction"
  check "encrypted adapter balance" 200 '"balance":1000' "$ENC_ADAPTER/balance"
  check "encrypted adapter validate" 200 'true' -X POST "$ENC_ADAPTER/validate-transaction"
}

group_forwarder() {
  if ! curl -s -o /dev/null --max-time 2 "$FORWARDER_ADMIN/ready"; then
    echo "SKIP forwarder (admin not reachable)"
    return
  fi
  check "forwarder ready" 200 '' "$FORWARDER_ADMIN/ready"
  check "forwarder: plain provider over HTTP" 200 '"balance":1000' -x "$FORWARDER" "http://plain-provider.localhost:5200/balance"
  check "forwarder: encrypted provider over CONNECT and TLS" 200 '"balance":1000' -x "$FORWARDER" "https://encrypted-provider.localhost:5300/balance"

  # Which way the adapters reach the providers. The Forwarder log is that of the running container: restart it (as
  # run-all.sh does) when switching PCI_PROXY_MODE, or lines from an earlier mode are still in it.
  local create='POST plain-provider\.localhost:5200/create-transaction'
  local validate='GET plain-provider\.localhost:5200/validate-transaction\?bankAccountNumber=012345678901234567'
  # In 'on' the adapter never has the raw number, so a pattern that names it could not match whatever happened: any validate.
  local validate_any='GET plain-provider\.localhost:5200/validate-transaction'
  local balance='GET plain-provider\.localhost:5200/balance'
  local connect='CONNECT encrypted-provider\.localhost:5300'
  local before new all
  before=$(podman logs sandbox-forwarder 2>&1 | wc -l)
  curl -s -o /dev/null -X POST "$PLAIN_ADAPTER/create-transaction"
  curl -s -o /dev/null -X POST "$PLAIN_ADAPTER/validate-transaction"
  curl -s -o /dev/null "$PLAIN_ADAPTER/balance"
  curl -s -o /dev/null -X POST "$ENC_ADAPTER/create-transaction"
  curl -s -o /dev/null "$ENC_ADAPTER/balance"
  sleep 1 # Envoy flushes its access log to stdout every 100 ms (scripts/forwarder.sh)
  all=$(podman logs sandbox-forwarder 2>&1)
  new=$(tail -n +$((before + 1)) <<< "$all")

  # The group's own curl checks above also pass through the Forwarder. .NET's HttpClient sends no User-Agent, which
  # the access log shows as "ua=-", so only those lines are the adapters'.
  local adapter_new adapter_all
  adapter_new=$(grep -E ' ua=-$' <<< "$new")
  adapter_all=$(grep -E ' ua=-$' <<< "$all")
  case $PCI_PROXY_MODE in
    egress)
      check_log "egress: plain create goes through the Forwarder" present "$create" "$adapter_new"
      check_log "egress: plain validate goes through the Forwarder" present "$validate" "$adapter_new"
      check_log "egress: plain balance goes through the Forwarder" present "$balance" "$adapter_new"
      # A pooled tunnel may be reused, so the whole log is searched for the CONNECT.
      check_log "egress: encrypted provider is reached over CONNECT" present "$connect" "$adapter_all"
      check_log "egress: the tokenizer never goes through the Forwarder" absent 'detokenize|:5100' "$all"
      ;;
    off)
      check_log "off: plain create does not use the Forwarder" absent "$create" "$adapter_new"
      check_log "off: plain validate does not use the Forwarder" absent "$validate" "$adapter_new"
      check_log "off: plain balance does not use the Forwarder" absent "$balance" "$adapter_new"
      check_log "off: encrypted create does not use the Forwarder" absent "$connect" "$adapter_new"
      check_log "off: the plain provider never went through the Forwarder" absent 'plain-provider\.localhost' "$adapter_all"
      check_log "off: the encrypted provider never went through the Forwarder" absent "$connect" "$adapter_all"
      ;;
    on)
      # The requests with a marker go to the PCI Proxy, which calls the providers directly; only balance, which has no
      # marker, still goes through the Forwarder.
      check_log "on: plain create goes to the PCI Proxy, not through the Forwarder" absent "$create" "$adapter_new"
      check_log "on: plain validate goes to the PCI Proxy, not through the Forwarder" absent "$validate_any" "$adapter_new"
      check_log "on: plain balance goes through the Forwarder" present "$balance" "$adapter_new"
      # The encrypted adapter's balance is the only request of its own that reaches the Forwarder; a pooled tunnel may be
      # reused, so the whole log is searched for the CONNECT.
      check_log "on: encrypted provider is reached over CONNECT (balance)" present "$connect" "$adapter_all"
      check_log "on: neither the tokenizer nor the PCI Proxy goes through the Forwarder" absent 'detokenize|:5100|:5700' "$all"
      ;;
    *)
      echo "PCI_PROXY_MODE must be 'off', 'egress' or 'on' (got '$PCI_PROXY_MODE')" >&2
      exit 2
      ;;
  esac
}

group_pci_proxy() {
  local M='{{pci:ba:Y000000000000001}}' ME='%7B%7Bpci%3Aba%3AY000000000000001%7D%7D'
  local TX_M="{\"transactionId\":\"T-000\",\"amount\":1.00,\"currency\":\"USD\",\"senderName\":\"A\",\"recipientName\":\"B\",\"bankAccountNumber\":\"$M\"}"
  local provider=http://plain-provider.localhost:5200 url=$SEND/plain-provider/send h='content-type: application/json'
  check_proxy "plain create, marker in body" 200 Forwarded '' '"status":"accepted"' \
    -X POST "$url" -H "x-proxy-url: $provider/create-transaction" -H "$h" -d "$TX_M"
  check_proxy "plain validate, marker in query" 200 Forwarded '' 'true' \
    "$url" -H "x-proxy-url: $provider/validate-transaction?bankAccountNumber=$ME"
  check_proxy "provider 404 passes through" 404 Forwarded '' '' \
    "$url" -H "x-proxy-url: $provider/no-such-route"
  check_proxy "method is preserved" 405 Forwarded '' '' \
    -X PUT "$url" -H "x-proxy-url: $provider/create-transaction" -H "$h" -d "$TX_M"
  check_proxy "missing X-Proxy-Url" 400 NotSent MARKER_SYNTAX '' -X POST "$url"
  check_proxy "bad X-Proxy-TimeoutMs" 400 NotSent MARKER_SYNTAX '' \
    "$url" -H "x-proxy-url: $provider/balance" -H 'x-proxy-timeoutms: abc'

  # A marker written into a URL with {{ }} is sent by .NET as %7B%7Bpci:ba:...%7D%7D (braces escaped, colons not): a
  # partly percent-encoded marker is still a marker, whole or malformed.
  local mixed='%7B%7Bpci:ba:Y000000000000001%7D%7D' query="$provider/validate-transaction?bankAccountNumber="
  check_proxy "partly encoded marker in query" 200 Forwarded '' 'true' "$url" -H "x-proxy-url: $query$mixed"
  check_proxy "partly encoded empty marker" 400 NotSent MARKER_SYNTAX '' "$url" -H "x-proxy-url: $query%7B%7Bpci:ba:%7D%7D"
  check_proxy "partly encoded truncated marker" 400 NotSent MARKER_SYNTAX '' "$url" -H "x-proxy-url: $query%7B%7Bpci:ba:Y000"
  check_proxy "partly encoded X-type token" 400 NotSent MARKER_SYNTAX '' \
    "$url" -H "x-proxy-url: ${query}%7B%7Bpci:ba:X000000000000001%7D%7D"

  # The error rows that need nothing but the running stack. A provider that does not answer (504, Unknown) and a tokenizer
  # that is down (503) are checked by hand: see Task 2 of the plan.
  local create=$provider/create-transaction
  check_proxy "empty marker" 400 NotSent MARKER_SYNTAX '' \
    -X POST "$url" -H "x-proxy-url: $create" -H "$h" -d '{"bankAccountNumber":"{{pci:ba:}}"}'
  check_proxy "truncated marker" 400 NotSent MARKER_SYNTAX '' \
    -X POST "$url" -H "x-proxy-url: $create" -H "$h" -d '{"bankAccountNumber":"{{pci:ba:Y000'
  check_proxy "X-type token" 400 NotSent MARKER_SYNTAX '' \
    -X POST "$url" -H "x-proxy-url: $create" -H "$h" -d '{"bankAccountNumber":"{{pci:ba:X000000000000001}}"}'
  check_proxy "truncated encoded marker in the URL" 400 NotSent MARKER_SYNTAX '' \
    "$url" -H "x-proxy-url: $provider/validate-transaction?bankAccountNumber=%7B%7Bpci%3Aba%3A"
  local invalid='{"bankAccountNumber":"{{pci:ba:Y000000000000000}}"}'
  check_proxy "invalid token" 422 NotSent TOKEN_INVALID '' -X POST "$url" -H "x-proxy-url: $create" -H "$h" -d "$invalid"
  check_proxy "provider unreachable" 502 NotSent PROVIDER_UNREACHABLE '' "$url" -H 'x-proxy-url: http://127.0.0.1:5999/x'
  check_proxy "unknown provider id is plain" 200 Forwarded '' '"balance":1000' \
    "$SEND/some-other-id/send" -H "x-proxy-url: $provider/balance"
  check_proxy "proxy error body is problem details" 422 NotSent TOKEN_INVALID '"status":422' \
    -X POST "$url" -H "x-proxy-url: $create" -H "$h" -d "$invalid"
  # HttpClient cannot write a non-ASCII header value, so nothing is sent: an invalid header, not a provider failure.
  check_proxy "non-ASCII header value" 400 NotSent MARKER_SYNTAX '' "$url" -H "x-proxy-url: $provider/balance" -H $'x-custom: caf\xc3\xa9'

  # The encrypted profile: the proxy replaces the markers, then encrypts the body, and trusts the provider's certificate.
  # The encrypted provider answers 200 only to a properly encrypted body, so it is the oracle for the encryption. The
  # plain profile (any other id) has no pinned certificate, so the handshake with the self-signed provider fails.
  local enc_url=$SEND/encrypted-provider/send enc_provider=https://encrypted-provider.localhost:5300
  local validate="{\"bankAccountNumber\":\"$M\"}"
  check_proxy "encrypted validate through the encrypted profile" 200 Forwarded '' 'true' \
    -X POST "$enc_url" -H "x-proxy-url: $enc_provider/validate-transaction" -H "$h" -d "$validate"
  check_proxy "encrypted create through the encrypted profile" 200 Forwarded '' '"status":"accepted"' \
    -X POST "$enc_url" -H "x-proxy-url: $enc_provider/create-transaction" -H "$h" -d "$TX_M"
  check_proxy "plain profile does not encrypt" 502 NotSent PROVIDER_UNREACHABLE '' \
    -X POST "$url" -H "x-proxy-url: $enc_provider/validate-transaction" -H "$h" -d "$validate"
  check_proxy "encrypted provider rejects a bad payload through the proxy" 400 Forwarded '' '' \
    -X POST "$enc_url" -H "x-proxy-url: $enc_provider/create-transaction" -H "$h" -d 'not json'
}

group_will_call() {
  local raw='012345678901234567'
  # The adapter alone: raw numbers when it detokenizes itself ('off', 'egress'), markers when the PCI Proxy is to ('on').
  if [[ $PCI_PROXY_MODE == on ]]; then
    check "will-call adapter returns markers" 200 '{{pci:ba:Y000000000000002}}' "$WILL_CALL_ADAPTER/lookup-transactions"
  else
    check "will-call adapter returns raw numbers" 200 "\"bankAccountNumber\":\"$raw\"" "$WILL_CALL_ADAPTER/lookup-transactions"
  fi
  # Through the PCI Proxy's YARP route the client gets raw numbers in either mode, and never a marker.
  local body
  body=$(curl -s "$PCI_PROXY/will-call/lookup-transactions")
  if [[ $body == *'T-101-willcall'* && $body == *'T-102-willcall'* && $body != *'{{pci:'* && $(grep -o "$raw" <<< "$body" | wc -l) -eq 2 ]]; then
    echo "PASS will-call via the PCI Proxy: two transactions, raw numbers"
  else
    echo "FAIL will-call via the PCI Proxy ($body)"
    FAILED=1
  fi
  check "will-call swagger UI via the PCI Proxy" 200 'swagger' -L "$PCI_PROXY/will-call/swagger"
  check "will-call swagger document names the /will-call server" 200 '"url": "/will-call"' "$PCI_PROXY/will-call/swagger/v1/swagger.json"
  check "will-call: unknown route passes through" 404 '' "$PCI_PROXY/will-call/no-such-route"
  check "will-call: marker in the query is refused" 400 'MARKER_SYNTAX' "$PCI_PROXY/will-call/lookup-transactions?x=%7B%7Bpci%3Aba%3AY000000000000002%7D%7D"
}

groups=("$@")
if [[ ${#groups[@]} -eq 0 ]]; then
  groups=($(compgen -A function group_ | sed 's/^group_//; s/_/-/g'))
fi
for g in "${groups[@]}"; do
  fn="group_${g//-/_}"
  if ! declare -F "$fn" > /dev/null; then
    echo "unknown group: $g" >&2
    exit 2
  fi
  "$fn"
done
exit $FAILED

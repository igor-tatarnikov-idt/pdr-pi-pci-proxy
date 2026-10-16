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
  local balance='GET plain-provider\.localhost:5200/balance'
  local connect='CONNECT encrypted-provider\.localhost:5300'
  local before new all
  before=$(podman logs sandbox-forwarder 2>&1 | wc -l)
  curl -s -o /dev/null -X POST "$PLAIN_ADAPTER/create-transaction"
  curl -s -o /dev/null -X POST "$PLAIN_ADAPTER/validate-transaction"
  curl -s -o /dev/null "$PLAIN_ADAPTER/balance"
  curl -s -o /dev/null -X POST "$ENC_ADAPTER/create-transaction"
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
    *)
      echo "PCI_PROXY_MODE must be 'off' or 'egress' (got '$PCI_PROXY_MODE')" >&2
      exit 2
      ;;
  esac
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

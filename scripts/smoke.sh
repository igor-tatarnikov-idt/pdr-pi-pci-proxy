#!/usr/bin/env bash
# Curl smoke checks for the PCI Proxy sandbox. The apps must already be running.
# Usage: scripts/smoke.sh [group ...]   (no argument runs every group)

TOKENIZER=http://127.0.0.1:5100
PLAIN_PROVIDER=http://127.0.0.1:5200
ENC_PROVIDER=http://127.0.0.1:5300
PLAIN_ADAPTER=http://127.0.0.1:5400
ENC_ADAPTER=http://127.0.0.1:5500
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
  out=$(curl -s -w $'\n%{http_code}' "$@")
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

group_tokenizer() {
  check "tokenizer detokenizes to the constant" 200 '"value":"012345678901234567"' \
    -X POST "$TOKENIZER/detokenize" -H 'content-type: application/json' -d '{"token":"anything"}'
}

group_plain_provider() {
  check "plain provider accepts transaction" 200 '"status":"accepted"' \
    -X POST "$PLAIN_PROVIDER/create-transaction" -H 'content-type: application/json' -d "$SAMPLE_TX"
  check "plain provider balance" 200 '"balance":1000' "$PLAIN_PROVIDER/balance"
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
}

group_plain_adapter() {
  check "plain adapter create" 200 '"status":"accepted"' -X POST "$PLAIN_ADAPTER/create-transaction"
  check "plain adapter balance" 200 '"balance":1000' "$PLAIN_ADAPTER/balance"
}

group_encrypted_adapter() {
  check "encrypted adapter create" 200 '"status":"accepted"' -X POST "$ENC_ADAPTER/create-transaction"
  check "encrypted adapter balance" 200 '"balance":1000' "$ENC_ADAPTER/balance"
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

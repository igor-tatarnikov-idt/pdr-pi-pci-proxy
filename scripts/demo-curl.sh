#!/usr/bin/env bash
# The demo's curl set. Run it in another terminal while scripts/demo-off.sh or scripts/demo-on.sh is running.
# Usage: scripts/demo-curl.sh off|on
# Every call carries an X-Request-Id (demo-<case>), so that scripts/logs.sh --id demo-<case> shows what it did in every app.

cd "$(dirname "$0")/.." || exit 1
MODE=${1:-}
if [[ $MODE != off && $MODE != on ]]; then
  echo "usage: $0 off|on" >&2
  exit 2
fi

PLAIN_ADAPTER=http://plain-adapter.localhost:5400
ENC_ADAPTER=http://encrypted-adapter.localhost:5500
WILL_CALL=http://will-call-adapter.localhost:5800
PCI_PROXY=http://pci-proxy.localhost:5700

# run <what to look for> <request id> <curl args...>: prints the command, then status line, headers and (shortened) body.
run() {
  local note=$1 id=$2
  shift 2
  echo
  echo "# $note"
  echo "\$ curl -i $* -H 'X-Request-Id: $id'"
  curl -sS -i -m 30 "$@" -H "X-Request-Id: $id" | cut -c1-300
  echo
}

section() { printf '\n==================== %s ====================\n' "$1"; }

section "Plain adapter (PCI Proxy mode $MODE)"
if [[ $MODE == off ]]; then
  note="the adapter calls the tokenizer, then the provider with the raw account number"
else
  note="no tokenizer call in the adapter: it sends a marker to the PCI Proxy, which detokenizes and calls the provider"
fi
run "create: $note" demo-plain-create -X POST "$PLAIN_ADAPTER/create-transaction"
run "validate: the account number is in the provider's query string" demo-plain-validate -X POST "$PLAIN_ADAPTER/validate-transaction"
run "balance: no account number, so no marker: through the Forwarder in 'on'" demo-plain-balance "$PLAIN_ADAPTER/balance"

section "Encrypted adapter (PCI Proxy mode $MODE)"
if [[ $MODE == off ]]; then
  note="the adapter detokenizes and encrypts the payload itself"
else
  note="the adapter sends plain JSON with a marker; the PCI Proxy detokenizes AND encrypts it"
fi
run "create: $note" demo-enc-create -X POST "$ENC_ADAPTER/create-transaction"
run "validate" demo-enc-validate -X POST "$ENC_ADAPTER/validate-transaction"
run "balance" demo-enc-balance "$ENC_ADAPTER/balance"

section "Will Call"
if [[ $MODE == off ]]; then
  run "DIRECT to the adapter (mode off): it detokenizes, so the raw account numbers come back" demo-willcall-direct "$WILL_CALL/lookup-transactions"
else
  run "DIRECT to the adapter (mode on): it returns markers, never a raw number" demo-willcall-direct "$WILL_CALL/lookup-transactions"
fi
run "VIA the PCI Proxy: the same call; the raw account numbers come back (the proxy replaces markers if there are any)" demo-willcall-proxy "$PCI_PROXY/will-call/lookup-transactions"

section "Will Call Swagger, through the PCI Proxy"
echo
echo "# the Swagger UI page of the Will Call adapter, served by the PCI Proxy (follows the redirect)"
echo "\$ curl -sS -L -o /dev/null -w 'HTTP %{http_code} %{content_type}' $PCI_PROXY/will-call/swagger"
curl -sS -L -m 30 -o /dev/null -w 'HTTP %{http_code} %{content_type}\n' "$PCI_PROXY/will-call/swagger"
echo
echo "# the Swagger document: its server is /will-call, so 'Try it out' goes through the PCI Proxy"
echo "\$ curl -sS $PCI_PROXY/will-call/swagger/v1/swagger.json | jq '{title: .info.title, servers, paths: (.paths | keys)}'"
curl -sS -m 30 "$PCI_PROXY/will-call/swagger/v1/swagger.json" | jq '{title: .info.title, servers, paths: (.paths | keys)}' 2>/dev/null \
  || echo "(install jq to see this shortened; or run the curl without the pipe)"
echo
echo "Open in a browser: $PCI_PROXY/will-call/swagger"
echo "Follow one call in all the logs, sorted by time: scripts/logs.sh --id demo-plain-create"

#!/usr/bin/env bash
# Demo setup 1 of 2: PCI Proxy mode OFF. Starts the whole sandbox with every adapter in 'off' mode: the adapters detokenize the
# bank account themselves and send the raw number to their providers, and the Will Call adapter returns raw account numbers to
# whoever calls it (call it directly). The PCI Proxy still runs, for the /will-call route and its Swagger.
# Then, in another terminal: scripts/demo-curl.sh off
cd "$(dirname "$0")/.." || exit 1
echo "== Demo setup: PCI Proxy mode OFF (adapters detokenize on their own; Will Call is called directly) =="
PciProxy__Mode=off exec scripts/run-all.sh

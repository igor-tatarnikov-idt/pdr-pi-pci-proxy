#!/usr/bin/env bash
# Demo setup 2 of 2: PCI Proxy mode ON. Starts the whole sandbox with every adapter in 'on' mode: the adapters never call the
# tokenizer, they put a marker where the raw account number was, and the PCI Proxy replaces it on the way to the provider.
# The Will Call adapter returns markers; the client calls the PCI Proxy at /will-call, which replaces them with raw numbers.
# Then, in another terminal: scripts/demo-curl.sh on
cd "$(dirname "$0")/.." || exit 1
echo "== Demo setup: PCI Proxy mode ON (adapters send markers; the PCI Proxy detokenizes; Will Call is called via the PCI Proxy) =="
PciProxy__Mode=on exec scripts/run-all.sh

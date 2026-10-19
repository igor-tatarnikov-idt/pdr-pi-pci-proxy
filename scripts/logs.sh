#!/usr/bin/env bash
# Merges the logs of all apps (logs/<App>.log, written by scripts/run-all.sh) into one output sorted by time.
# Usage: scripts/logs.sh [--id REQUEST_ID] [--app NAME] [--json]
#   --id    only the lines of one request (its X-Request-Id: the demo curls send ids such as demo-plain-create)
#   --app   only one app (Tokenizer, PciProxy, Forwarder, ...)
#   --json  the raw JSON lines, sorted, instead of the readable form
cd "$(dirname "$0")/.." || exit 1
exec python3 -I scripts/merge-logs.py logs "$@"

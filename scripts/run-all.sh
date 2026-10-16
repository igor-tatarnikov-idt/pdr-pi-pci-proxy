#!/usr/bin/env bash
# Builds the solution once, then starts all five sandbox apps with every log line prefixed by the app name.
# Ctrl+C (or SIGTERM) stops them all and waits until they have exited and their output is flushed.

cd "$(dirname "$0")/.." || exit 1
dotnet build PciProxySandbox.sln --nologo -v quiet || exit 1

APPS=(Tokenizer PlainProvider EncryptedProvider PlainAdapter EncryptedAdapter)
PIDS=()
FIFO_DIR=$(mktemp -d)

cleanup() {
  trap - INT TERM EXIT
  kill "${PIDS[@]}" 2>/dev/null
  wait # the apps first, then the prefixers once the apps have closed their pipes
  rm -rf "$FIFO_DIR"
}
trap cleanup INT TERM EXIT

for app in "${APPS[@]}"; do
  mkfifo "$FIFO_DIR/$app"
  awk -v prefix="[$app] " '{ print prefix $0; fflush() }' < "$FIFO_DIR/$app" &
  # Run from the project folder so appsettings.json is found; exec makes $! the app's own PID.
  ( cd "src/$app" && exec dotnet "bin/Debug/net10.0/$app.dll" ) > "$FIFO_DIR/$app" 2>&1 &
  PIDS+=($!)
done

wait

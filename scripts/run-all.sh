#!/usr/bin/env bash
# Creates the sandbox certificate if it is missing, builds the solution once, starts the Forwarder (an Envoy container
# that needs Podman), then starts all seven sandbox apps, with every log line prefixed by the app name.
# Without Podman it only warns: the sandbox then works in 'off' mode but not in 'egress' mode.
# Ctrl+C (or SIGTERM) stops them all, removes the Forwarder container, and waits until the apps have exited and their
# output is flushed.

cd "$(dirname "$0")/.." || exit 1
scripts/gen-cert.sh || exit 1
dotnet build PciProxySandbox.sln --nologo -v quiet || exit 1

# A failed start prints why on stderr. Podman being absent or stopped is not fatal: 'off' mode needs no Forwarder.
FORWARDER_STARTED=
if out=$(scripts/forwarder.sh start 2>&1); then
  echo "$out"
  FORWARDER_STARTED=1
else
  echo "warning: Forwarder not started (${out//$'\n'/ }); egress mode will not work" >&2
  scripts/forwarder.sh stop > /dev/null 2>&1 # a container that started but never became ready must not linger
fi

APPS=(Tokenizer PciProxy PlainProvider EncryptedProvider PlainAdapter EncryptedAdapter WillCallAdapter)
PIDS=()
FIFO_DIR=$(mktemp -d)

cleanup() {
  trap - INT TERM EXIT
  kill "${PIDS[@]}" 2>/dev/null
  if [[ -n $FORWARDER_STARTED ]]; then
    scripts/forwarder.sh stop # without Podman this is never reached: stop has no guard of its own
  fi
  wait # the apps first, then the prefixers once the apps have closed their pipes
  rm -rf "$FIFO_DIR"
}
trap cleanup INT TERM EXIT

# prefixed <name>: prints everything written to that name's FIFO with the prefix [name]
prefixed() {
  mkfifo "$FIFO_DIR/$1"
  awk -v prefix="[$1] " '{ print prefix $0; fflush() }' < "$FIFO_DIR/$1" &
}

if [[ -n $FORWARDER_STARTED ]]; then
  prefixed Forwarder
  scripts/forwarder.sh logs > "$FIFO_DIR/Forwarder" 2>&1 &
  PIDS+=($!)
  disown $! # it is killed at exit, which bash would report as "Terminated"; the apps exit on their own
fi

for app in "${APPS[@]}"; do
  prefixed "$app"
  # Run from the project folder so appsettings.json is found; exec makes $! the app's own PID.
  ( cd "src/$app" && exec dotnet "bin/Debug/net10.0/$app.dll" ) > "$FIFO_DIR/$app" 2>&1 &
  PIDS+=($!)
done

wait

#!/usr/bin/env bash
# Runs the Forwarder: an Envoy container (sandbox-forwarder) acting as a forward proxy for the adapters' provider calls.
# The proxy is published on 127.0.0.1:5600 and Envoy's admin interface on 127.0.0.1:5601.
# Usage: scripts/forwarder.sh start|stop|logs

cd "$(dirname "$0")/.." || exit 1

NAME=sandbox-forwarder
IMAGE=docker.io/envoyproxy/envoy:v1.36.9
ADMIN=http://127.0.0.1:5601

start() {
  if ! command -v podman > /dev/null; then
    echo "podman is not installed" >&2
    exit 1
  fi
  if ! podman info > /dev/null 2>&1; then
    echo "podman is not running (is the machine started? try: podman machine start)" >&2
    exit 1
  fi

  podman rm -f "$NAME" > /dev/null || exit 1

  # The provider host names must resolve inside the container to the host machine. host-gateway does not work in
  # this Podman setup, so ask the image what host.containers.internal is.
  local host
  host=$(podman run --rm --entrypoint getent "$IMAGE" hosts host.containers.internal | awk '{ print $1; exit }')
  if [[ -z $host ]]; then
    echo "cannot resolve host.containers.internal inside the $IMAGE container" >&2
    exit 1
  fi

  # The image's own entrypoint fails under this Podman setup, so run the binary directly.
  podman run -d --name "$NAME" \
    -p 127.0.0.1:5600:5600 -p 127.0.0.1:5601:9901 \
    --add-host "plain-provider.localhost:$host" --add-host "encrypted-provider.localhost:$host" \
    -v "$PWD/forwarder/envoy.yaml:/etc/envoy/envoy.yaml:ro" \
    --entrypoint envoy "$IMAGE" -c /etc/envoy/envoy.yaml --log-level warn --file-flush-interval-msec 100 > /dev/null || exit 1

  local i
  for i in $(seq 1 20); do
    if [[ $(curl -s -o /dev/null -w '%{http_code}' --max-time 1 "$ADMIN/ready") == 200 ]]; then
      echo "Forwarder ready: proxy http://127.0.0.1:5600, admin $ADMIN"
      return 0
    fi
    sleep 1
  done
  echo "Forwarder did not become ready within 20 s; container logs:" >&2
  podman logs "$NAME" >&2
  exit 1
}

case $1 in
  start) start ;;
  stop) podman rm -f "$NAME" > /dev/null ;;
  logs) exec podman logs -f "$NAME" ;;
  *)
    echo "usage: scripts/forwarder.sh start|stop|logs" >&2
    exit 2
    ;;
esac

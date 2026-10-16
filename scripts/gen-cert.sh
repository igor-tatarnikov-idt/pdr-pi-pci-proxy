#!/usr/bin/env bash
# Creates the self-signed certificate the EncryptedProvider serves over HTTPS: certs/sandbox.crt and
# certs/sandbox.key. Does nothing if both files exist. Nothing is added to any system trust store.

cd "$(dirname "$0")/.." || exit 1

if [[ -f certs/sandbox.crt && -f certs/sandbox.key ]]; then
  echo "certs already exist"
  exit 0
fi

mkdir -p certs
CONFIG=$(mktemp)
LOG=$(mktemp)
trap 'rm -f "$CONFIG" "$LOG"' EXIT
cat > "$CONFIG" <<'CONF'
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = encrypted-provider.localhost
[ext]
subjectAltName = DNS:encrypted-provider.localhost, DNS:localhost, IP:127.0.0.1
extendedKeyUsage = serverAuth
CONF

# openssl prints key-generation progress on stderr; show it only if the command fails.
if ! openssl req -x509 -newkey rsa:2048 -nodes -days 825 \
  -keyout certs/sandbox.key -out certs/sandbox.crt -config "$CONFIG" 2> "$LOG"; then
  cat "$LOG" >&2
  rm -f certs/sandbox.key certs/sandbox.crt
  exit 1
fi
echo "created certs/sandbox.crt and certs/sandbox.key"

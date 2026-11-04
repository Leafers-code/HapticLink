#!/bin/sh
# Event bridge must be enabled in HapticLink; output also needs to be armed.
set -eu
curl --fail --silent --show-error \
  -H 'Content-Type: application/json' \
  -d '{"event":"kill"}' \
  http://127.0.0.1:12346/event

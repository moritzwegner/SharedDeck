#!/bin/bash
# Builds the SharedDeck WASM module in the FlyByWire dev-env container (needs Docker Desktop running).
set -e

IMAGE="ghcr.io/flybywiresim/dev-env@sha256:28b1f55c047b9ec338c3d676a82225fe135b0b1061fa7993c03b9a75b5e470cd"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

export MSYS_NO_PATHCONV=1
docker run --rm -v "$ROOT":/external "$IMAGE" bash /external/wasm/build-in-container.sh

#!/usr/bin/env bash
# Freeze an MSBuild bootstrap SDK under $RESTORE_BENCH/sdks/<label>. Debug and Release builds share
# artifacts/bin/bootstrap/core, so always measure a snapshot, never the live bootstrap.
#
# usage: snapshot-sdk.sh <label> [msbuild-repo]
#   Build first with ./build.sh -c Release; the bootstrap SDK version must match runtime's global.json.
set -euo pipefail
source "$(dirname "$0")/env.sh"

LABEL=$1
REPO=${2:-$(cd "$SCRIPTS_DIR/../.." && pwd)}
SRC=$REPO/artifacts/bin/bootstrap/core
VER=$(detect_sdk_version)
[[ -d $SRC/sdk/$VER ]] || { echo "bootstrap has no SDK $VER (runtime pins it); found: $(ls "$SRC/sdk")" >&2; exit 1; }
rm -rf "${SDKS:?}/$LABEL"
cp -a "$SRC" "$SDKS/$LABEL"
echo "$SDKS/$LABEL ($(git -C "$REPO" log -1 --format=%h) $(strings -el "$SRC/sdk/$VER/Microsoft.Build.dll" | grep -m1 -xE 'Release|Debug'))"

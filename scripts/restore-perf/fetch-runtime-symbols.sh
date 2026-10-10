#!/usr/bin/env bash
# Download debug symbols for an SDK's libcoreclr.so (stripped in the SDK) from the Microsoft symbol
# server by ELF build id, and install them in perf's build-id cache. Run after one perf-record.sh so
# perf has cached the binary.
#
# usage: fetch-runtime-symbols.sh <sdk>
#   For a Native AOT image, copy its <name>.dbg the same way: cp app.dbg ~/.debug/.build-id/xx/<rest>/debug
set -euo pipefail
source "$(dirname "$0")/env.sh"

SDK=$1
[[ -d $SDK ]] || SDK=$SDKS/$SDK
LIB=$SDK/shared/Microsoft.NETCore.App/$(detect_runtime_version "$SDK")/libcoreclr.so
ID=$(readelf -n "$LIB" | awk '/Build ID/ {print $3}')
CACHE=$HOME/.debug/.build-id/${ID:0:2}/${ID:2}
[[ -e $CACHE ]] || perf buildid-cache -a "$LIB"
DIR=$(readlink -f "$CACHE")
curl -sSfL -o "$DIR/debug" "https://msdl.microsoft.com/download/symbols/_.debug/elf-buildid-sym-$ID/_.debug"
echo "installed symbols for $LIB ($ID)"

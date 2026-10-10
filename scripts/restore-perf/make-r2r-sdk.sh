#!/usr/bin/env bash
# Copy an SDK snapshot and ReadyToRun-compile the MSBuild assemblies the bootstrap replaces, matching
# how the shipping SDK precompiles them. Uses the crossgen2 package that matches the SDK's runtime.
#
# usage: make-r2r-sdk.sh <source-snapshot> <dest-snapshot>
set -euo pipefail
source "$(dirname "$0")/env.sh"

SRC=$1; DEST=$2
[[ -d $SRC ]] || SRC=$SDKS/$SRC
[[ $DEST == */* ]] || DEST=$SDKS/$DEST
VER=$(detect_sdk_version)
FX=$(detect_runtime_version "$SRC")
CROSSGEN=$PACKAGES/microsoft.netcore.app.crossgen2.linux-x64/$FX/tools/crossgen2
[[ -x $CROSSGEN ]] || { echo "missing $CROSSGEN (runtime's restore normally downloads it)" >&2; exit 1; }
ASSEMBLIES=(Microsoft.Build Microsoft.Build.Framework Microsoft.Build.Tasks.Core Microsoft.Build.Utilities.Core Microsoft.NET.StringTools MSBuild)

rm -rf "$DEST"
cp -a "$SRC" "$DEST"
SDKDIR=$DEST/sdk/$VER
FXDIR=$DEST/shared/Microsoft.NETCore.App/$FX
IL=$(mktemp -d)
trap 'rm -rf "$IL"' EXIT
for a in "${ASSEMBLIES[@]}"; do cp "$SDKDIR/$a.dll" "$IL/$a.dll"; done
for a in "${ASSEMBLIES[@]}"; do
  refs=(-r "$FXDIR/*.dll")
  for b in "${ASSEMBLIES[@]}"; do [[ $b == "$a" ]] || refs+=(-r "$IL/$b.dll"); done
  for dep in "$SDKDIR"/System.*.dll; do [[ -f $FXDIR/$(basename "$dep") ]] || refs+=(-r "$dep"); done
  "$CROSSGEN" "$IL/$a.dll" -o "$SDKDIR/$a.dll" --targetos:linux --targetarch:x64 -O "${refs[@]}" > /dev/null
done
echo "ReadyToRun snapshot: $DEST"

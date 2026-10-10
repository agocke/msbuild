#!/usr/bin/env bash
# Publish the Native AOT NuGet console host against an SDK snapshot's assemblies, then create a new
# snapshot that contains it in sdk/<version>/ (where the workload SDK resolver expects to run).
#
# usage: aot-host/build.sh <source-snapshot> <dest-snapshot>
# then:  BENCH_ARGS="-p:NuGetConsoleProcessFileName=$RESTORE_BENCH/sdks/<dest>/sdk/<version>/RestoreAotHost" run.sh <dest> <out>
set -euo pipefail
source "$(dirname "$0")/../env.sh"

SRC=$1; DEST=$2
[[ -d $SRC ]] || SRC=$SDKS/$SRC
[[ $DEST == */* ]] || DEST=$SDKS/$DEST
VER=$(detect_sdk_version)
HOST=$SCRIPTS_DIR/aot-host
printf '{ "sdk": { "version": "%s", "rollForward": "disable" } }\n' "$VER" > "$HOST/global.json"
NUGET_PACKAGES=$PACKAGES/ DOTNET_CLI_TELEMETRY_OPTOUT=1 "$RT/.dotnet/dotnet" publish "$HOST/RestoreAotHost.csproj" \
  -c Release -v:q -nologo -p:SdkDir="$(realpath "$SRC")/sdk/$VER"
rm -rf "$DEST"
cp -a "$SRC" "$DEST"
cp "$HOST/bin/Release/net"*/linux-x64/publish/RestoreAotHost "$DEST/sdk/$VER/"
echo "$DEST/sdk/$VER/RestoreAotHost"

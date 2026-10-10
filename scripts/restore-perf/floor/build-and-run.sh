#!/usr/bin/env bash
# Build the floor (reference implementation) with Native AOT and replay an evaluation trace.
#
# usage: floor/build-and-run.sh <trace-dir> [repetitions]
#   Record a trace with: MSBUILDEVALUATIONTRACE=<dir> run.sh <sdk> <out>   (the directory must exist)
#   FLOOR_MISMATCHES=1 lists conditions whose replayed result differs from the recorded one.
set -euo pipefail
source "$(dirname "$0")/../env.sh"

FLOOR=$SCRIPTS_DIR/floor
printf '{ "sdk": { "version": "%s", "rollForward": "disable" } }\n' "$(detect_sdk_version)" > "$FLOOR/global.json"
NUGET_PACKAGES=$PACKAGES/ DOTNET_CLI_TELEMETRY_OPTOUT=1 "$RT/.dotnet/dotnet" publish "$FLOOR/Floor.csproj" -c Release -v:q -nologo > /dev/null
"$FLOOR"/bin/Release/net*/linux-x64/publish/Floor "$1" "${2:-3}"

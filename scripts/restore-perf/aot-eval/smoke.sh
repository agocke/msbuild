#!/usr/bin/env bash
# usage: bash aot-eval/smoke.sh <snapshot> <build-work-dir>
set -euo pipefail
source "$(dirname "$0")/../env.sh"
[[ $# -eq 2 ]] || { echo "usage: $0 <snapshot> <build-work-dir>" >&2; exit 1; }
sdk=$1
[[ -d $sdk ]] || sdk=$SDKS/$sdk
sdk=$(realpath "$sdk")
work=$(realpath "$2")
sdk_dir=$sdk/sdk/$(detect_sdk_version)
export NUGET_PACKAGES=$PACKAGES/
"$sdk/dotnet" build "$work/host/Smoke.csproj" -c Release -v:q -nologo \
  -p:SdkDir="$sdk_dir" -o "$work/smoke"
for mode in managed roundtrip native; do
  mkdir -p "$work/scratch-$mode"
  MSBUILD_RESTORE_EVAL_MODE=$mode MSBUILD_RESTORE_EVAL_SUMMARY=$work/smoke-$mode \
    "$sdk/dotnet" "$work/smoke/Smoke.dll" "$sdk_dir" "$work/scratch-$mode"
done

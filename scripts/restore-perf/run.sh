#!/usr/bin/env bash
# One clean, package-cached static-graph restore of dotnet/runtime with a given SDK snapshot.
#
# usage: run.sh <sdk-dir> <out-dir> [wrapper command...]
#   <sdk-dir>  an SDK snapshot directory or label (see snapshot-sdk.sh)
#   <out-dir>  receives console.log and result.txt
#   wrapper    optional prefix, e.g.: perf stat -x, -o "$OUT/perf-stat.csv" -e instructions:u,instructions:k --
#
# Environment passes through (MSBUILDCOSTLEDGER, MSBUILDEVALUATIONTRACE, DOTNET_TieredPGO, ...).
# BENCH_ARGS adds MSBuild arguments, e.g. -p:GenerateRestoreUseStaticGraphEvaluationBinlog=false.
set -euo pipefail
source "$(dirname "$0")/env.sh"

SDK=$1
[[ -d $SDK ]] || SDK=$SDKS/$SDK   # a snapshot label
SDK=$(realpath "$SDK")
OUT=$(realpath -m "$2")
shift 2
VER=$(detect_sdk_version)
TOOLSET_PROJ=$(detect_toolset_proj)
[[ -n $TOOLSET_PROJ ]] || { echo "no Arcade toolset; run setup.sh" >&2; exit 1; }

if (( $(cat /proc/sys/kernel/perf_event_paranoid) > 1 )); then
  echo "warning: kernel.perf_event_paranoid > 1; instruction counts will exclude kernel work" >&2
fi
mkdir -p "$OUT"
rm -rf "$RT/artifacts/obj" "$RT/artifacts/log/Debug/Restore-Build.proj.binlog"

export NUGET_PACKAGES=$PACKAGES/
export DOTNET_ROOT=$SDK
export DOTNET_MULTILEVEL_LOOKUP=0
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
export MSBUILDUSESERVER=0

cd "$RT"
start=$(date +%s.%N)
set +e
# The arguments runtime's build.sh -restore passes, minus the CLI logger and the outer binlog.
"$@" "$SDK/dotnet" "$SDK/sdk/$VER/MSBuild.dll" \
  -maxcpucount -nr:false --verbosity:minimal --nologo -clp:Summary -tl:false \
  --property:AdditionalWarningsNotAsErrors=NU1901%3BNU1902%3BNU1903%3BNU1904 \
  --property:TreatWarningsAsErrors=true --property:ContinuousIntegrationBuild=false \
  --property:Configuration=Debug --property:RepoRoot=$RT/ \
  --property:Restore=true --property:Build=false --property:DotNetBuild=false \
  --property:DotNetBuildSourceOnly=false --property:DotNetBuildFromVMR=false \
  --property:Rebuild=false --property:Test=false --property:Pack=false \
  --property:IntegrationTest=false --property:PerformanceTest=false --property:Sign=false \
  --property:Publish=false --property:RestoreStaticGraphEnableBinaryLogger=false \
  --property:FeatureDynamicCodeCompiled=true --property:Ninja=true \
  --property:TargetArchitecture=x64 --property:BuildArchitecture=x64 --property:CMakeArgs= \
  -warnaserror -warnnotaserror:NU1901\;NU1902\;NU1903\;NU1904 \
  ${BENCH_ARGS:-} "$TOOLSET_PROJ" > "$OUT/console.log" 2>&1
status=$?
set -e
end=$(date +%s.%N)

assets=$(find "$RT/artifacts/obj" -name project.assets.json | wc -l)
binlog=$([[ -f "$RT/artifacts/log/Debug/Restore-Build.proj.binlog" ]] && echo yes || echo no)
wall=$(echo "$end - $start" | bc)
printf 'exit=%s wall=%s assets=%s restore_binlog=%s\n' "$status" "$wall" "$assets" "$binlog" | tee "$OUT/result.txt"
(cd "$RT/artifacts/obj" && find . -name project.assets.json -print0 | sort -z | xargs -0 sha256sum) > "$OUT/assets.sha256"
[[ $status -eq 0 ]]

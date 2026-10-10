#!/usr/bin/env bash
# Record an instruction profile of one restore with symbolized JIT code.
#
# usage: perf-record.sh <sdk> <out-dir>   (BENCH_ARGS and other environment pass through)
# Then: perf script -i <out-dir>/perf.data --pid <console pid> -F comm,tid,period,ip,sym,dso | analysis/classify.py
#
# DOTNET_EnableWriteXorExecute=0 is required: with W^X on, JIT code runs from a memfd mapping and perf
# never consults /tmp/perf-<pid>.map. ReadyToRun code runs from the file-mapped DLL and is likewise
# unsymbolized, which breaks call-stack attribution, so attribution runs JIT everything
# (DOTNET_ReadyToRun=0; override with PERF_READYTORUN=1). Use matrix.sh, not this, for timing.
# Run fetch-runtime-symbols.sh once per SDK for native runtime frames.
set -euo pipefail
source "$(dirname "$0")/env.sh"

SDK=$1; OUT=$(realpath -m "$2")
[[ -d $SDK ]] || SDK=$SDKS/$SDK
mkdir -p "$OUT"
DOTNET_ReadyToRun=${PERF_READYTORUN:-0} DOTNET_PerfMapEnabled=1 DOTNET_EnableWriteXorExecute=0 "$SCRIPTS_DIR/run.sh" "$SDK" "$OUT" \
  perf record -q -e instructions -c 1000003 --call-graph fp -o "$OUT/perf.data" --
perf report -i "$OUT/perf.data" --no-children --sort comm -q --stdio -g none 2>/dev/null | head -8

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

# Sample on instructions retired when the CPU exposes hardware counters; otherwise (for example, a VM
# without a virtual PMU) on the software cpu-clock. Sample weights are then nanoseconds, so the analysis
# scripts' totals (units of 1e9) read as CPU seconds. PERF_EVENT=instructions|cpu-clock overrides.
EVENT=${PERF_EVENT:-}
if [[ -z $EVENT ]]; then
  if perf stat -x, -e instructions -- true 2>&1 | grep -qE '^[0-9]+,,instructions'; then EVENT=instructions; else EVENT=cpu-clock; fi
fi
case $EVENT in
  instructions) SAMPLE=(-e instructions -c 1000003) ;;
  cpu-clock) SAMPLE=(-e cpu-clock -c 100000) ;;     # one sample per 100 us of CPU time
  *) echo "PERF_EVENT must be instructions or cpu-clock" >&2; exit 1 ;;
esac
echo "$EVENT" > "$OUT/event"
echo "sampling on $EVENT" >&2

DOTNET_ReadyToRun=${PERF_READYTORUN:-0} DOTNET_PerfMapEnabled=1 DOTNET_EnableWriteXorExecute=0 "$SCRIPTS_DIR/run.sh" "$SDK" "$OUT" \
  perf record -q "${SAMPLE[@]}" --call-graph fp -o "$OUT/perf.data" --
perf report -i "$OUT/perf.data" --no-children --sort comm -q --stdio -g none 2>/dev/null | head -8

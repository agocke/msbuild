#!/usr/bin/env bash
# Interleaved perf-stat comparison of restore configurations.
#
# usage: matrix.sh <name> <repetitions> <config>...
#   config = label|sdk|ENV=VALUE ...|msbuild args
#   sdk is a snapshot label under $RESTORE_BENCH/sdks (or a path).
#
# example:
#   matrix.sh pgo 3 \
#     "pgo-on|r2r||-p:GenerateRestoreUseStaticGraphEvaluationBinlog=false" \
#     "pgo-off|r2r|DOTNET_TieredPGO=0|-p:GenerateRestoreUseStaticGraphEvaluationBinlog=false"
#
# Each repetition runs every configuration once, in order, so drift affects all of them alike.
set -uo pipefail
source "$(dirname "$0")/env.sh"

NAME=$1; REPS=$2; shift 2
EVENTS=instructions:u,instructions:k,cycles:u,cycles:k,task-clock
for rep in $(seq 1 "$REPS"); do
  for config in "$@"; do
    IFS='|' read -r label sdk envs args <<< "$config"
    [[ -d $sdk ]] || sdk=$SDKS/$sdk
    out=$RESULTS/$NAME/$label-$rep
    mkdir -p "$out"
    # shellcheck disable=SC2086
    env $envs BENCH_ARGS="$args" "$SCRIPTS_DIR/run.sh" "$sdk" "$out" \
      perf stat -x, -o "$out/perf-stat.csv" -e "$EVENTS" -- > /dev/null
    echo "$label rep $rep: $(cat "$out/result.txt")"
  done
done
python3 "$SCRIPTS_DIR/analysis/summarize.py" "$RESULTS/$NAME"

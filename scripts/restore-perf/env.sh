#!/usr/bin/env bash
# Shared settings for the restore performance scripts. Source this file; do not run it.
#
#   RESTORE_BENCH     benchmark root (default ~/restore-bench): runtime clone, packages, SDK snapshots, results
#   RUNTIME_COMMIT    dotnet/runtime commit the workload uses
#
# Versions are detected from the runtime clone so nothing is pinned to one machine.

RESTORE_BENCH=${RESTORE_BENCH:-$HOME/restore-bench}
RUNTIME_COMMIT=${RUNTIME_COMMIT:-3b5416d32b67c2cb63f69a91975620de8ea0c13d}
SCRIPTS_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

RT=$RESTORE_BENCH/runtime
PACKAGES=$RESTORE_BENCH/packages
SDKS=$RESTORE_BENCH/sdks
RESULTS=$RESTORE_BENCH/results

# SDK version pinned by the runtime clone, e.g. 11.0.100-rc.1.26420.103.
detect_sdk_version() {
  python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['sdk']['version'])" "$RT/global.json"
}

# Microsoft.NETCore.App version shipped in an SDK layout.
detect_runtime_version() {
  ls "$1/shared/Microsoft.NETCore.App" | sort -V | tail -1
}

# Arcade toolset Build.proj that runtime's build.sh restores through (created by setup.sh).
detect_toolset_proj() {
  ls -d "$RT"/artifacts/toolset/*/Build.proj 2>/dev/null | sort -V | tail -1
}

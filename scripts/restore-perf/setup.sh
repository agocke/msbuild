#!/usr/bin/env bash
# One-time setup: clone dotnet/runtime at RUNTIME_COMMIT and warm an isolated package cache by running
# runtime's own restore once with its pinned SDK.
#
# usage: setup.sh [runtime-source]
#   runtime-source  a local clone to copy from (fast) or a URL (default https://github.com/dotnet/runtime)
set -euo pipefail
source "$(dirname "$0")/env.sh"

SOURCE=${1:-https://github.com/dotnet/runtime}
mkdir -p "$RESTORE_BENCH" "$PACKAGES" "$SDKS" "$RESULTS"

if [[ ! -d $RT/.git ]]; then
  if [[ -d $SOURCE ]]; then
    git clone -q "$SOURCE" "$RT"
  else
    git clone -q --filter=blob:none "$SOURCE" "$RT"
  fi
fi
git -C "$RT" checkout -q "$RUNTIME_COMMIT"
echo "runtime at $(git -C "$RT" log -1 --format='%h %s')"

# Downloads the pinned SDK into runtime/.dotnet, the Arcade toolset, and every package.
(cd "$RT" && NUGET_PACKAGES=$PACKAGES/ ./build.sh -restore)
echo "toolset: $(detect_toolset_proj)"
echo "SDK: $(detect_sdk_version)"

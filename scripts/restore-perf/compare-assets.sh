#!/usr/bin/env bash
# Compare the restore outputs of two runs, ignoring the SDK snapshot directory embedded in the assets.
# run.sh records raw and normalized hashes before later runs overwrite the assets.
#
# usage: compare-assets.sh <out-dir-a> <out-dir-b>
set -euo pipefail
diff <(awk '{print $2}' "$1/assets.sha256") <(awk '{print $2}' "$2/assets.sha256") > /dev/null \
  || { echo "different asset file sets"; exit 1; }
if cmp -s "$1/assets.sha256" "$2/assets.sha256"; then
  echo "identical"
elif [[ -f "$1/assets.normalized.sha256" && -f "$2/assets.normalized.sha256" ]] &&
     cmp -s "$1/assets.normalized.sha256" "$2/assets.normalized.sha256"; then
  echo "identical after normalizing SDK snapshot paths"
else
  echo "$(diff "$1/assets.sha256" "$2/assets.sha256" | grep -c '^<') files differ by hash."
  echo "Runs with different SDK snapshots embed different SDK paths (runtimeIdentifierGraphPath); to check"
  echo "content, copy both obj trees aside and diff with 'sed s#/sdks/[^/]*/#/sdks/X/#g'."
  exit 1
fi

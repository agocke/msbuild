#!/usr/bin/env bash
# usage: bash aot-eval/build.sh <source-snapshot> <new-snapshot> <new-work-dir>
set -euo pipefail
source "$(dirname "$0")/../env.sh"
[[ $# -eq 3 ]] || { echo "usage: $0 <source-snapshot> <new-snapshot> <new-work-dir>" >&2; exit 1; }
src=$1
[[ -d $src ]] || src=$SDKS/$src
src=$(realpath "$src")
dest=$2
[[ $dest == */* ]] || dest=$SDKS/$dest
dest=$(realpath -m "$dest")
work=$(realpath -m "$3")
[[ ! -e $dest ]] || { echo "Refusing to overwrite $dest" >&2; exit 1; }
repo=$(git -C "$SCRIPTS_DIR" rev-parse --show-toplevel)
host=$SCRIPTS_DIR/aot-eval
ver=$(detect_sdk_version)
[[ -f $src/sdk/$ver/Microsoft.Build.dll ]] || { echo "Snapshot SDK version mismatch" >&2; exit 1; }

python3 "$host/prepare.py" "$repo" "$work"
tree=$work/worktree
# Reuse restored package inputs, but keep all compiler output isolated.
mkdir -p "$tree/artifacts/obj"
cp -a "$repo/artifacts/obj/Microsoft.Build" "$tree/artifacts/obj/"
cp -a "$repo/artifacts/obj/Microsoft.Build.Framework" "$tree/artifacts/obj/"
cp -a "$repo/artifacts/obj/StringTools" "$tree/artifacts/obj/"
mkdir -p "$tree/artifacts/bin"
cp -a "$repo/artifacts/bin/Microsoft.Build.Framework" "$tree/artifacts/bin/"
cp -a "$repo/artifacts/bin/StringTools" "$tree/artifacts/bin/"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
env -u NUGET_PACKAGES "$src/dotnet" msbuild "$tree/src/Build/Microsoft.Build.csproj" -t:Build -v:q -nologo \
  -p:Configuration=Release -p:TargetFramework=net11.0 -p:BuildProjectReferences=false \
  -p:GeneratePackageOnBuild=false -p:CreateBootstrap=false \
  -p:CustomAfterMicrosoftCommonTargets="$host/Bridge.targets" -p:RestoreEvalWork="$work"

# Hardlinks conserve disk; replace the engine by unlinking, never write into a shared inode.
cp -al "$src" "$dest"
sdk_dir=$dest/sdk/$ver
engine=$tree/artifacts/bin/Microsoft.Build/Release/net11.0/Microsoft.Build.dll
cp --remove-destination "$engine" "$sdk_dir/Microsoft.Build.dll"
mkdir -p "$work/host"
cp "$host/"*.csproj "$host/"*.cs "$host/Directory."* "$work/host/"
export NUGET_PACKAGES=$PACKAGES/
"$src/dotnet" publish "$work/host/RestoreAotEvaluation.csproj" -c Release -v:q -nologo \
  -p:SdkDir="$sdk_dir" -o "$work/publish"
cp --remove-destination "$work/publish/libRestoreAotEvaluation.so" "$sdk_dir/RestoreAotEvaluation.so"
sha256sum "$sdk_dir/Microsoft.Build.dll" | cut -d' ' -f1 > "$work/native.engine.sha256"
cp --remove-destination "$work/native.engine.sha256" "$sdk_dir/RestoreAotEvaluation.so.engine.sha256"
cp --remove-destination "$host/RestoreSplitHost" "$sdk_dir/"
chmod +x "$sdk_dir/RestoreSplitHost"
printf '%s\n' "$sdk_dir/RestoreSplitHost"

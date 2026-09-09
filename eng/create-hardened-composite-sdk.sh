#!/usr/bin/env bash

set -euo pipefail

source="${BASH_SOURCE[0]}"
while [[ -h "$source" ]]; do
  script_root="$(cd -P "$(dirname "$source")" && pwd)"
  source="$(readlink "$source")"
  [[ "$source" != /* ]] && source="$script_root/$source"
done

script_root="$(cd -P "$(dirname "$source")" && pwd)"
msbuild_repo="$(cd "$script_root/.." && pwd -P)"
sdk_repo="$msbuild_repo/../sdk"
sourcelink_repo="$msbuild_repo/../sourcelink"
output_dir="$msbuild_repo/artifacts/bin/hardened-composite-sdk"
configuration="Debug"
sdk_version=""
msbuild_tfm="net11.0"
sourcelink_tfm="net10.0"
sourcelink_framework_tfm="net472"
build_repositories=true

usage() {
  cat <<'EOF'
Usage: eng/create-hardened-composite-sdk.sh [options]

Builds and combines local MSBuild, SDK, and SourceLink checkouts into one SDK.

Options:
  --msbuild-repo PATH          MSBuild checkout (default: this repository)
  --sdk-repo PATH              SDK checkout (default: ../sdk)
  --sourcelink-repo PATH       SourceLink checkout (default: ../sourcelink)
  --output PATH                Composite output directory
  --configuration NAME         Build configuration (default: Debug)
  --sdk-version VERSION        SDK directory to compose; auto-detected by default
  --msbuild-tfm TFM            MSBuild output TFM (default: net11.0)
  --sourcelink-tfm TFM         SourceLink .NET output TFM (default: net10.0)
  --sourcelink-framework-tfm TFM
                                SourceLink .NET Framework output TFM (default: net472)
  --no-build                   Compose existing build outputs without rebuilding
  -h, --help                   Show this help
EOF
}

fail() {
  echo "error: $*" >&2
  exit 1
}

require_directory() {
  [[ -d "$1" ]] || fail "directory not found: $1"
}

require_file() {
  [[ -f "$1" ]] || fail "file not found: $1"
}

canonicalize_directory() {
  (
    cd "$1"
    pwd -P
  )
}

canonicalize_output() {
  local path="$1"
  local parent
  local name

  parent="$(dirname "$path")"
  name="$(basename "$path")"
  mkdir -p "$parent"
  parent="$(canonicalize_directory "$parent")"
  printf '%s/%s\n' "$parent" "$name"
}

repository_revision() {
  local repo="$1"
  local revision

  revision="$(git -C "$repo" rev-parse HEAD)"
  if [[ -n "$(git -C "$repo" status --porcelain)" ]]; then
    revision="$revision-dirty"
  fi

  printf '%s\n' "$revision"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --msbuild-repo)
      msbuild_repo="$2"
      shift 2
      ;;
    --sdk-repo)
      sdk_repo="$2"
      shift 2
      ;;
    --sourcelink-repo)
      sourcelink_repo="$2"
      shift 2
      ;;
    --output)
      output_dir="$2"
      shift 2
      ;;
    --configuration)
      configuration="$2"
      shift 2
      ;;
    --sdk-version)
      sdk_version="$2"
      shift 2
      ;;
    --msbuild-tfm)
      msbuild_tfm="$2"
      shift 2
      ;;
    --sourcelink-tfm)
      sourcelink_tfm="$2"
      shift 2
      ;;
    --sourcelink-framework-tfm)
      sourcelink_framework_tfm="$2"
      shift 2
      ;;
    --no-build)
      build_repositories=false
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      fail "unknown argument: $1"
      ;;
  esac
done

require_directory "$msbuild_repo"
require_directory "$sdk_repo"
require_directory "$sourcelink_repo"

msbuild_repo="$(canonicalize_directory "$msbuild_repo")"
sdk_repo="$(canonicalize_directory "$sdk_repo")"
sourcelink_repo="$(canonicalize_directory "$sourcelink_repo")"
output_dir="$(canonicalize_output "$output_dir")"

case "$output_dir" in
  /|"$msbuild_repo"|"$sdk_repo"|"$sourcelink_repo")
    fail "refusing unsafe output directory: $output_dir"
    ;;
esac

if [[ "$build_repositories" == true ]]; then
  echo "Building MSBuild..."
  (
    cd "$msbuild_repo"
    ./build.sh --configuration "$configuration" -v quiet
  )

  echo "Building SDK..."
  (
    cd "$sdk_repo"
    ./build.sh --configuration "$configuration"
  )

  echo "Building SourceLink..."
  (
    cd "$sourcelink_repo"
    ./build.sh --configuration "$configuration"
  )
fi

sdk_redist="$sdk_repo/artifacts/bin/redist/$configuration/dotnet"
require_directory "$sdk_redist/sdk"

if [[ -z "$sdk_version" ]]; then
  for candidate in "$sdk_redist/sdk"/*; do
    [[ -f "$candidate/MSBuild.dll" ]] || continue
    if [[ -n "$sdk_version" ]]; then
      fail "multiple SDK versions found under $sdk_redist/sdk; pass --sdk-version"
    fi
    sdk_version="$(basename "$candidate")"
  done
fi

[[ -n "$sdk_version" ]] || fail "no SDK version found under $sdk_redist/sdk"

sdk_payload="$sdk_redist/sdk/$sdk_version"
msbuild_payload="$msbuild_repo/artifacts/bin/MSBuild/$configuration/$msbuild_tfm"
msbuild_bootstrap_root="$msbuild_repo/artifacts/bin/bootstrap/core"
sourcelink_payload="$sourcelink_repo/artifacts/bin/Microsoft.Build.Tasks.Git/$configuration/$sourcelink_tfm/publish"
sourcelink_framework_payload="$sourcelink_repo/artifacts/bin/Microsoft.Build.Tasks.Git/$configuration/$sourcelink_framework_tfm"
sourcelink_package_source="$sourcelink_repo/src/Microsoft.Build.Tasks.Git"

require_file "$sdk_payload/MSBuild.dll"
require_file "$sdk_payload/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.targets"
require_file "$msbuild_payload/MSBuild.dll"
require_file "$msbuild_payload/Microsoft.Common.CurrentVersion.targets"
require_directory "$msbuild_bootstrap_root/sdk"
require_file "$sourcelink_payload/Microsoft.Build.Tasks.Git.dll"
require_file "$sourcelink_package_source/build/Microsoft.Build.Tasks.Git.targets"

msbuild_bootstrap_sdk=""
for candidate in "$msbuild_bootstrap_root/sdk"/*; do
  [[ -f "$candidate/MSBuild.dll" ]] || continue
  if [[ -n "$msbuild_bootstrap_sdk" ]]; then
    fail "multiple SDK versions found under $msbuild_bootstrap_root/sdk"
  fi
  msbuild_bootstrap_sdk="$candidate"
done

[[ -n "$msbuild_bootstrap_sdk" ]] || fail "no bootstrap SDK found under $msbuild_bootstrap_root/sdk"

stage_dir="$output_dir.tmp.$$"
cleanup() {
  rm -rf "$stage_dir"
}
trap cleanup EXIT

rm -rf "$stage_dir"
mkdir -p "$stage_dir"

echo "Copying SDK redist..."
cp -a "$sdk_redist/." "$stage_dir/"

composite_sdk="$stage_dir/sdk/$sdk_version"

echo "Overlaying MSBuild..."
rsync -a --exclude '/Roslyn/' --exclude '/Sdks/' "$msbuild_bootstrap_sdk/" "$composite_sdk/"

echo "Overlaying SourceLink..."
for package_directory in build buildMultiTargeting buildTransitive; do
  source_directory="$sourcelink_package_source/$package_directory"
  if [[ -d "$source_directory" ]]; then
    mkdir -p "$composite_sdk/Sdks/Microsoft.Build.Tasks.Git/$package_directory"
    rsync -a "$source_directory/" "$composite_sdk/Sdks/Microsoft.Build.Tasks.Git/$package_directory/"
  fi
done

mkdir -p "$composite_sdk/Sdks/Microsoft.Build.Tasks.Git/tools/net"
rsync -a "$sourcelink_payload/" "$composite_sdk/Sdks/Microsoft.Build.Tasks.Git/tools/net/"

if [[ -d "$sourcelink_framework_payload" ]]; then
  mkdir -p "$composite_sdk/Sdks/Microsoft.Build.Tasks.Git/tools/netframework"
  for file in Microsoft.Build.Tasks.Git.dll System.IO.Hashing.dll; do
    if [[ -f "$sourcelink_framework_payload/$file" ]]; then
      cp "$sourcelink_framework_payload/$file" "$composite_sdk/Sdks/Microsoft.Build.Tasks.Git/tools/netframework/$file"
    fi
  done
fi

cat > "$stage_dir/msbuild-hardened" <<EOF
#!/usr/bin/env bash
set -euo pipefail
root="\$(cd -P "\$(dirname "\${BASH_SOURCE[0]}")" && pwd)"
export DOTNET_ROOT="\$root"
export DOTNET_MULTILEVEL_LOOKUP=0
export DOTNET_HOST_PATH="\$root/dotnet"
export DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR="\$root"
export MSBuildSDKsPath="\$root/sdk/$sdk_version/Sdks"
exec "\$root/dotnet" "\$root/sdk/$sdk_version/MSBuild.dll" "\$@"
EOF
chmod +x "$stage_dir/msbuild-hardened"

cat > "$stage_dir/component-versions.txt" <<EOF
configuration=$configuration
sdk-version=$sdk_version
msbuild=$(repository_revision "$msbuild_repo")
sdk=$(repository_revision "$sdk_repo")
sourcelink=$(repository_revision "$sourcelink_repo")
EOF

cmp -s "$msbuild_payload/MSBuild.dll" "$composite_sdk/MSBuild.dll" ||
  fail "composite MSBuild.dll does not match the local MSBuild build"
cmp -s "$msbuild_payload/Microsoft.Common.CurrentVersion.targets" "$composite_sdk/Microsoft.Common.CurrentVersion.targets" ||
  fail "composite common targets do not match the local MSBuild build"
cmp -s "$sdk_payload/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.targets" "$composite_sdk/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.targets" ||
  fail "composite SDK targets do not match the local SDK build"
cmp -s "$sourcelink_package_source/build/Microsoft.Build.Tasks.Git.targets" "$composite_sdk/Sdks/Microsoft.Build.Tasks.Git/build/Microsoft.Build.Tasks.Git.targets" ||
  fail "composite SourceLink targets do not match the local SourceLink checkout"
cmp -s "$sourcelink_payload/Microsoft.Build.Tasks.Git.dll" "$composite_sdk/Sdks/Microsoft.Build.Tasks.Git/tools/net/Microsoft.Build.Tasks.Git.dll" ||
  fail "composite SourceLink task assembly does not match the local SourceLink build"

"$stage_dir/msbuild-hardened" -version -nologo >/dev/null

rm -rf "$output_dir"
mv "$stage_dir" "$output_dir"
trap - EXIT

echo
echo "Composite SDK created at:"
echo "  $output_dir"
echo
echo "Run a hardened build with:"
echo "  $output_dir/msbuild-hardened <project> -restore --hardened-graph -m:1 -nr:false"

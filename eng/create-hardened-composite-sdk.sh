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
roslyn_repo="$msbuild_repo/../roslyn"
output_dir="$msbuild_repo/artifacts/bin/hardened-composite-sdk"
configuration="Debug"
sdk_version=""
msbuild_tfm="net11.0"
sourcelink_tfm="net10.0"
sourcelink_framework_tfm="net472"
roslyn_tfm="net10.0"
build_repositories=true

usage() {
  cat <<'EOF'
Usage: eng/create-hardened-composite-sdk.sh [options]

Builds and combines local MSBuild, SDK, SourceLink, and Roslyn checkouts into one SDK.

Options:
  --msbuild-repo PATH          MSBuild checkout (default: this repository)
  --sdk-repo PATH              SDK checkout (default: ../sdk)
  --sourcelink-repo PATH       SourceLink checkout (default: ../sourcelink)
  --roslyn-repo PATH           Roslyn checkout (default: ../roslyn)
  --output PATH                Composite output directory
  --configuration NAME         Build configuration (default: Debug)
  --sdk-version VERSION        SDK directory to compose; auto-detected by default
  --msbuild-tfm TFM            MSBuild output TFM (default: net11.0)
  --sourcelink-tfm TFM         SourceLink .NET output TFM (default: net10.0)
  --sourcelink-framework-tfm TFM
                                SourceLink .NET Framework output TFM (default: net472)
  --roslyn-tfm TFM             Roslyn build task output TFM (default: net10.0)
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
    --roslyn-repo)
      roslyn_repo="$2"
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
    --roslyn-tfm)
      roslyn_tfm="$2"
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
require_directory "$roslyn_repo"

msbuild_repo="$(canonicalize_directory "$msbuild_repo")"
sdk_repo="$(canonicalize_directory "$sdk_repo")"
sourcelink_repo="$(canonicalize_directory "$sourcelink_repo")"
roslyn_repo="$(canonicalize_directory "$roslyn_repo")"
output_dir="$(canonicalize_output "$output_dir")"

case "$output_dir" in
  /|"$msbuild_repo"|"$sdk_repo"|"$sourcelink_repo"|"$roslyn_repo")
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

  echo "Building Roslyn MSBuild tasks..."
  (
    cd "$roslyn_repo"
    dotnet build src/Compilers/Core/MSBuildTask/MSBuild/Microsoft.Build.Tasks.CodeAnalysis.csproj --configuration "$configuration" --framework "$roslyn_tfm"
    dotnet build src/CodeStyle/Tools/CodeStyleConfigFileGenerator.csproj --configuration "$configuration" --framework "$roslyn_tfm"
    dotnet build src/CodeStyle/CSharp/CodeFixes/Microsoft.CodeAnalysis.CSharp.CodeStyle.Fixes.csproj --configuration "$configuration" --framework netstandard2.0
    dotnet build src/CodeStyle/VisualBasic/CodeFixes/Microsoft.CodeAnalysis.VisualBasic.CodeStyle.Fixes.vbproj --configuration "$configuration" --framework netstandard2.0
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
sourcelink_payload="$sourcelink_repo/artifacts/bin/Microsoft.Build.Tasks.Git/$configuration/$sourcelink_tfm"
sourcelink_common_payload="$sourcelink_repo/artifacts/bin/Microsoft.SourceLink.Common/$configuration/$sourcelink_tfm"
roslyn_tasks_payload="$roslyn_repo/artifacts/bin/Microsoft.Build.Tasks.CodeAnalysis/$configuration/$roslyn_tfm"
roslyn_codestyle_generator="$roslyn_repo/artifacts/bin/CodeStyleConfigFileGenerator/$configuration/$roslyn_tfm/CodeStyleConfigFileGenerator.dll"
roslyn_codestyle_common_payload="$roslyn_repo/artifacts/bin/Microsoft.CodeAnalysis.CodeStyle/$configuration/netstandard2.0"
roslyn_codestyle_csharp_payload="$roslyn_repo/artifacts/bin/Microsoft.CodeAnalysis.CSharp.CodeStyle/$configuration/netstandard2.0"
roslyn_codestyle_csharp_fixes_payload="$roslyn_repo/artifacts/bin/Microsoft.CodeAnalysis.CSharp.CodeStyle.Fixes/$configuration/netstandard2.0"
roslyn_codestyle_visualbasic_payload="$roslyn_repo/artifacts/bin/Microsoft.CodeAnalysis.VisualBasic.CodeStyle/$configuration/netstandard2.0"
roslyn_codestyle_visualbasic_fixes_payload="$roslyn_repo/artifacts/bin/Microsoft.CodeAnalysis.VisualBasic.CodeStyle.Fixes/$configuration/netstandard2.0"
netanalyzers_payload="$sdk_repo/artifacts/bin/Microsoft.CodeAnalysis.NetAnalyzers.Package"

require_file "$sdk_payload/MSBuild.dll"
require_file "$sdk_payload/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.targets"
require_file "$msbuild_payload/MSBuild.dll"
require_file "$msbuild_payload/Microsoft.Common.CurrentVersion.targets"
require_directory "$msbuild_bootstrap_root/sdk"
require_file "$sourcelink_payload/Microsoft.Build.Tasks.Git.dll"
require_file "$sourcelink_repo/src/Microsoft.Build.Tasks.Git/build/Microsoft.Build.Tasks.Git.targets"
require_file "$sourcelink_common_payload/Microsoft.SourceLink.Common.dll"
require_file "$sourcelink_repo/src/SourceLink.Common/build/InitializeSourceControlInformation.targets"
require_file "$sourcelink_repo/src/SourceLink.Common/build/Microsoft.SourceLink.Common.targets"
require_file "$roslyn_tasks_payload/Microsoft.Build.Tasks.CodeAnalysis.dll"
require_file "$roslyn_codestyle_generator"
require_file "$roslyn_codestyle_common_payload/Microsoft.CodeAnalysis.CodeStyle.dll"
require_file "$roslyn_codestyle_csharp_payload/Microsoft.CodeAnalysis.CSharp.CodeStyle.dll"
require_file "$roslyn_codestyle_visualbasic_payload/Microsoft.CodeAnalysis.VisualBasic.CodeStyle.dll"
require_file "$netanalyzers_payload/Build/Microsoft.CodeAnalysis.NetAnalyzers.targets"
require_directory "$netanalyzers_payload/GlobalAnalyzerConfigs"

"$sdk_redist/dotnet" "$roslyn_codestyle_generator" \
  CSharp \
  "$roslyn_codestyle_csharp_fixes_payload" \
  Microsoft.CodeAnalysis.CSharp.CodeStyle.targets \
  "$roslyn_codestyle_common_payload/Microsoft.CodeAnalysis.CodeStyle.dll;$roslyn_codestyle_csharp_payload/Microsoft.CodeAnalysis.CSharp.CodeStyle.dll"

"$sdk_redist/dotnet" "$roslyn_codestyle_generator" \
  VisualBasic \
  "$roslyn_codestyle_visualbasic_fixes_payload" \
  Microsoft.CodeAnalysis.VisualBasic.CodeStyle.targets \
  "$roslyn_codestyle_common_payload/Microsoft.CodeAnalysis.CodeStyle.dll;$roslyn_codestyle_visualbasic_payload/Microsoft.CodeAnalysis.VisualBasic.CodeStyle.dll"

require_file "$roslyn_codestyle_csharp_fixes_payload/Microsoft.CodeAnalysis.CSharp.CodeStyle.targets"
require_directory "$roslyn_codestyle_csharp_fixes_payload/config"
require_file "$roslyn_codestyle_visualbasic_fixes_payload/Microsoft.CodeAnalysis.VisualBasic.CodeStyle.targets"
require_directory "$roslyn_codestyle_visualbasic_fixes_payload/config"

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
for package in Microsoft.Build.Tasks.Git Microsoft.SourceLink.Common Microsoft.SourceLink.AzureRepos.Git Microsoft.SourceLink.Bitbucket.Git Microsoft.SourceLink.GitHub Microsoft.SourceLink.GitLab; do
  if [[ "$package" == "Microsoft.Build.Tasks.Git" ]]; then
    package_source="$sourcelink_repo/src/Microsoft.Build.Tasks.Git"
  else
    package_source="$sourcelink_repo/src/${package#Microsoft.}"
  fi

  package_output="$sourcelink_repo/artifacts/bin/$package/$configuration"
  composite_package="$composite_sdk/Sdks/$package"

  [[ -d "$composite_package" ]] || continue
  require_directory "$package_source"

  for package_directory in build buildMultiTargeting buildTransitive; do
    source_directory="$package_source/$package_directory"
    if [[ -d "$source_directory" ]]; then
      mkdir -p "$composite_package/$package_directory"
      rsync -a "$source_directory/" "$composite_package/$package_directory/"
    fi
  done

  if [[ "$package" == "Microsoft.Build.Tasks.Git" ]]; then
    mkdir -p "$composite_package/tools/net"
    rsync -a --exclude '/publish/' "$sourcelink_payload/" "$composite_package/tools/net/"
    if [[ -f "$sourcelink_payload/publish/System.IO.Hashing.dll" ]]; then
      cp "$sourcelink_payload/publish/System.IO.Hashing.dll" "$composite_package/tools/net/"
    fi
  else
    require_directory "$package_output/$sourcelink_tfm"
    mkdir -p "$composite_package/tools/net"
    rsync -a "$package_output/$sourcelink_tfm/" "$composite_package/tools/net/"
  fi

  if [[ -d "$package_output/$sourcelink_framework_tfm" ]]; then
    mkdir -p "$composite_package/tools/netframework"
    rsync -a "$package_output/$sourcelink_framework_tfm/" "$composite_package/tools/netframework/"
  fi
done

echo "Overlaying Roslyn MSBuild tasks..."
cp "$roslyn_tasks_payload/Microsoft.Build.Tasks.CodeAnalysis.dll" "$composite_sdk/Roslyn/Microsoft.Build.Tasks.CodeAnalysis.dll"

echo "Overlaying analyzer globalconfig manifests..."
cp "$roslyn_codestyle_csharp_fixes_payload/Microsoft.CodeAnalysis.CSharp.CodeStyle.targets" \
  "$composite_sdk/Sdks/Microsoft.NET.Sdk/codestyle/cs/build/Microsoft.CodeAnalysis.CSharp.CodeStyle.targets"
rsync -a "$roslyn_codestyle_csharp_fixes_payload/config/" \
  "$composite_sdk/Sdks/Microsoft.NET.Sdk/codestyle/cs/build/config/"
cp "$roslyn_codestyle_visualbasic_fixes_payload/Microsoft.CodeAnalysis.VisualBasic.CodeStyle.targets" \
  "$composite_sdk/Sdks/Microsoft.NET.Sdk/codestyle/vb/build/Microsoft.CodeAnalysis.VisualBasic.CodeStyle.targets"
rsync -a "$roslyn_codestyle_visualbasic_fixes_payload/config/" \
  "$composite_sdk/Sdks/Microsoft.NET.Sdk/codestyle/vb/build/config/"
cp "$netanalyzers_payload/Build/Microsoft.CodeAnalysis.NetAnalyzers.targets" \
  "$composite_sdk/Sdks/Microsoft.NET.Sdk/analyzers/build/Microsoft.CodeAnalysis.NetAnalyzers.targets"
rsync -a "$netanalyzers_payload/GlobalAnalyzerConfigs/" \
  "$composite_sdk/Sdks/Microsoft.NET.Sdk/analyzers/build/config/"

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
roslyn=$(repository_revision "$roslyn_repo")
EOF

cmp -s "$msbuild_payload/MSBuild.dll" "$composite_sdk/MSBuild.dll" ||
  fail "composite MSBuild.dll does not match the local MSBuild build"
cmp -s "$msbuild_payload/Microsoft.Common.CurrentVersion.targets" "$composite_sdk/Microsoft.Common.CurrentVersion.targets" ||
  fail "composite common targets do not match the local MSBuild build"
cmp -s "$sdk_payload/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.targets" "$composite_sdk/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.targets" ||
  fail "composite SDK targets do not match the local SDK build"
cmp -s "$sourcelink_repo/src/Microsoft.Build.Tasks.Git/build/Microsoft.Build.Tasks.Git.targets" "$composite_sdk/Sdks/Microsoft.Build.Tasks.Git/build/Microsoft.Build.Tasks.Git.targets" ||
  fail "composite SourceLink targets do not match the local SourceLink checkout"
cmp -s "$sourcelink_payload/Microsoft.Build.Tasks.Git.dll" "$composite_sdk/Sdks/Microsoft.Build.Tasks.Git/tools/net/Microsoft.Build.Tasks.Git.dll" ||
  fail "composite SourceLink task assembly does not match the local SourceLink build"
cmp -s "$sourcelink_repo/src/SourceLink.Common/build/InitializeSourceControlInformation.targets" "$composite_sdk/Sdks/Microsoft.SourceLink.Common/build/InitializeSourceControlInformation.targets" ||
  fail "composite SourceLink initialization targets do not match the local SourceLink checkout"
cmp -s "$sourcelink_repo/src/SourceLink.Common/build/Microsoft.SourceLink.Common.targets" "$composite_sdk/Sdks/Microsoft.SourceLink.Common/build/Microsoft.SourceLink.Common.targets" ||
  fail "composite SourceLink common targets do not match the local SourceLink checkout"
cmp -s "$sourcelink_common_payload/Microsoft.SourceLink.Common.dll" "$composite_sdk/Sdks/Microsoft.SourceLink.Common/tools/net/Microsoft.SourceLink.Common.dll" ||
  fail "composite SourceLink common task assembly does not match the local SourceLink build"
cmp -s "$roslyn_tasks_payload/Microsoft.Build.Tasks.CodeAnalysis.dll" "$composite_sdk/Roslyn/Microsoft.Build.Tasks.CodeAnalysis.dll" ||
  fail "composite Roslyn task assembly does not match the local Roslyn build"
cmp -s "$roslyn_codestyle_csharp_fixes_payload/Microsoft.CodeAnalysis.CSharp.CodeStyle.targets" "$composite_sdk/Sdks/Microsoft.NET.Sdk/codestyle/cs/build/Microsoft.CodeAnalysis.CSharp.CodeStyle.targets" ||
  fail "composite C# CodeStyle targets do not match the local Roslyn build"
cmp -s "$roslyn_codestyle_visualbasic_fixes_payload/Microsoft.CodeAnalysis.VisualBasic.CodeStyle.targets" "$composite_sdk/Sdks/Microsoft.NET.Sdk/codestyle/vb/build/Microsoft.CodeAnalysis.VisualBasic.CodeStyle.targets" ||
  fail "composite Visual Basic CodeStyle targets do not match the local Roslyn build"
cmp -s "$netanalyzers_payload/Build/Microsoft.CodeAnalysis.NetAnalyzers.targets" "$composite_sdk/Sdks/Microsoft.NET.Sdk/analyzers/build/Microsoft.CodeAnalysis.NetAnalyzers.targets" ||
  fail "composite NetAnalyzers targets do not match the local SDK build"

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

#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="${GRAPHIFY_CSHARP_TEST_CONFIGURATION:-Release}"
sdk_line="$(dotnet --list-sdks | awk '$1 ~ /^10\./ { result=$0 } END { print result }')"
if [[ -z "$sdk_line" ]]; then
  echo "The .NET 10 SDK is required for the net10.0 tests." >&2
  exit 1
fi
sdk_version="${sdk_line%% *}"
sdk_directory="${sdk_line#*\[}"
sdk_directory="${sdk_directory%\]}"
dotnet_root="$(dirname -- "$sdk_directory")"
isolated_root="$(mktemp -d "${TMPDIR:-/tmp}/graphify-net10-tests.XXXXXX")"
trap 'rm -rf "$isolated_root"' EXIT

# Roslyn starts a separate build host with --roll-forward LatestMajor. Keep
# that host on .NET 10 as well as the test process, without reinstalling SDKs.
cp "$dotnet_root/dotnet" "$isolated_root/dotnet"
ln -s "$dotnet_root/host" "$isolated_root/host"
mkdir -p "$isolated_root/sdk"
ln -s "$sdk_directory/$sdk_version" "$isolated_root/sdk/$sdk_version"
for directory in shared/Microsoft.NETCore.App shared/Microsoft.AspNetCore.App; do
  mkdir -p "$isolated_root/$directory"
  for version_directory in "$dotnet_root/$directory"/10.*; do
    if [[ -d "$version_directory" ]]; then
      ln -s "$version_directory" "$isolated_root/$directory/$(basename -- "$version_directory")"
    fi
  done
done
for directory in packs sdk-manifests; do
  if [[ -d "$dotnet_root/$directory" ]]; then
    ln -s "$dotnet_root/$directory" "$isolated_root/$directory"
  fi
done

DOTNET_ROOT="$isolated_root" PATH="$isolated_root:$PATH" \
  "$isolated_root/dotnet" test "$repository_root/Graphify.CSharp.sln" \
  --configuration "$configuration" --framework net10.0 --no-build --no-restore "$@"

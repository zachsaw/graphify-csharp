#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
tool_framework="${GRAPHIFY_CSHARP_WATCH_E2E_FRAMEWORK:-net10.0}"
target_framework="${GRAPHIFY_CSHARP_WATCH_E2E_TARGET_FRAMEWORK:-net10.0}"
configuration="${GRAPHIFY_CSHARP_WATCH_E2E_CONFIGURATION:-Release}"
package_version="${GRAPHIFY_CSHARP_WATCH_E2E_PACKAGE_VERSION:-0.1.0-watcher-e2e}"
package_path="${GRAPHIFY_CSHARP_WATCH_E2E_PACKAGE_PATH:-}"
watch_scan_interval="${GRAPHIFY_CSHARP_WATCH_E2E_SCAN_INTERVAL:-00:00:01}"

temporary_base="${TMPDIR:-/tmp}"
temporary_base="${temporary_base%/}"
temporary_root="$(mktemp -d "$temporary_base/graphify-csharp-watcher-e2e.XXXXXX")"
fixture_root="$temporary_root/fixture"
feed_directory="$temporary_root/feed"
tool_directory="$temporary_root/tool"
output_path="$fixture_root/graphify-out/csharp.json"
alternate_output_path="$fixture_root/graphify-out/alternate.json"
watcher_log="$temporary_root/watcher.log"
client_log="$temporary_root/client.log"
watcher_pid=""
mkdir -p "$fixture_root" "$feed_directory" "$tool_directory"

cleanup() {
  if [[ -n "$watcher_pid" ]] && kill -0 "$watcher_pid" 2>/dev/null; then
    kill "$watcher_pid" 2>/dev/null || true
    wait "$watcher_pid" 2>/dev/null || true
  fi
  rm -rf "$temporary_root"
}
trap cleanup EXIT

cp -R "$repository_root/tests/Fixtures/ReferenceFixture/." "$fixture_root/"
# Keep the fixture copy clean even when the contributor's checkout has local
# build output from another test run.
rm -rf "$fixture_root/bin" "$fixture_root/obj"

dotnet restore "$fixture_root/ReferenceFixture.csproj"

if [[ -n "$package_path" ]]; then
  if [[ "$package_path" != /* ]]; then
    package_path="$repository_root/$package_path"
  fi
  package_path="$(cd -- "$(dirname -- "$package_path")" && pwd)/$(basename -- "$package_path")"
  test -f "$package_path"
  package_file="${package_path##*/}"
  case "$package_file" in
    Graphify.CSharp.*.nupkg)
      package_version="${package_file#Graphify.CSharp.}"
      package_version="${package_version%.nupkg}"
      ;;
    *)
      echo "Expected a Graphify.CSharp package path, got '$package_path'." >&2
      exit 1
      ;;
  esac
  cp -- "$package_path" "$feed_directory/"
else
  dotnet restore "$repository_root/Graphify.CSharp.sln"
  dotnet build "$repository_root/src/Graphify.CSharp.Cli/Graphify.CSharp.Cli.csproj" \
    --configuration "$configuration" \
    --framework "$tool_framework"
  dotnet pack "$repository_root/src/Graphify.CSharp.Cli/Graphify.CSharp.Cli.csproj" \
    --configuration "$configuration" \
    --output "$feed_directory" \
    -p:Version="$package_version" \
    -p:PackageVersion="$package_version"

  package_path="$feed_directory/Graphify.CSharp.${package_version}.nupkg"
  test -f "$package_path"
fi

dotnet tool install \
  --tool-path "$tool_directory" \
  --add-source "$feed_directory" \
  --ignore-failed-sources \
  --no-cache \
  --framework "$tool_framework" \
  Graphify.CSharp \
  --version "$package_version"

run_tool_at_output() {
  local requested_output_path="$1"
  shift
  "$tool_directory/graphify-csharp" \
    --input "$fixture_root/ReferenceFixture.csproj" \
    --root "$fixture_root" \
    --configuration "$configuration" \
    --target-framework "$target_framework" \
    --output "$requested_output_path" \
    "$@"
}

run_tool() {
  run_tool_at_output "$output_path" "$@"
}

start_watcher() {
  : > "$watcher_log"
  "$tool_directory/graphify-csharp" \
    --input "$fixture_root/ReferenceFixture.csproj" \
    --root "$fixture_root" \
    --configuration "$configuration" \
    --target-framework "$target_framework" \
    --output "$output_path" \
    --watch \
    --watch-scan-interval "$watch_scan_interval" > "$watcher_log" 2>&1 &
  watcher_pid=$!
  for _ in {1..120}; do
    if [[ -s "$output_path" ]] && grep -Fq 'Watching ' "$watcher_log"; then
      return
    fi

    if ! kill -0 "$watcher_pid" 2>/dev/null; then
      sed -n '1,160p' "$watcher_log" >&2 || true
      echo 'Watcher exited before becoming ready.' >&2
      exit 1
    fi

    sleep 1
  done

  sed -n '1,160p' "$watcher_log" >&2 || true
  echo 'Watcher did not become ready within two minutes.' >&2
  exit 1
}

stop_watcher() {
  if [[ -n "$watcher_pid" ]]; then
    kill "$watcher_pid" 2>/dev/null || true
    wait "$watcher_pid" 2>/dev/null || true
    watcher_pid=""
  fi
}

start_watcher
run_tool > "$client_log"
grep -Fq '(watcher,' "$client_log"
jq -e '.nodes | length > 0' "$output_path" >/dev/null
jq -e '.edges | length > 0' "$output_path" >/dev/null

cp "$output_path" "$temporary_root/before-change.json"
run_tool_at_output "$alternate_output_path" > "$client_log"
if grep -Fq '(watcher,' "$client_log"; then
  echo 'An alternate output request incorrectly attached to the canonical watcher.' >&2
  exit 1
fi
test -s "$alternate_output_path"
cmp -s "$temporary_root/before-change.json" "$output_path"

printf '\npublic sealed class BackupAndWarmRefreshChange { }\n' >> "$fixture_root/ReferenceTypes.cs"
sleep 2
cmp -s "$temporary_root/before-change.json" "$output_path"

run_tool > "$client_log"
grep -Fq '(watcher,' "$client_log"
grep -Fq 'ReferenceFixture.Production.BackupAndWarmRefreshChange' "$output_path"

stop_watcher
printf '\npublic sealed class RestartRecoveryChange { }\n' >> "$fixture_root/ReferenceTypes.cs"
start_watcher
grep -Fq 'ReferenceFixture.Production.RestartRecoveryChange' "$output_path"

run_tool --rebuild > "$client_log"
grep -Fq '(watcher,' "$client_log"
jq -e '.nodes | length > 0' "$output_path" >/dev/null
jq -e '.edges | length > 0' "$output_path" >/dev/null


node_count="$(jq '.nodes | length' "$output_path")"
edge_count="$(jq '.edges | length' "$output_path")"
echo "watcher-e2e-ok framework=$tool_framework package=$package_version nodes=$node_count edges=$edge_count"

#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
tool_framework="${GRAPHIFY_CSHARP_WATCH_E2E_FRAMEWORK:-net10.0}"
target_framework="${GRAPHIFY_CSHARP_WATCH_E2E_TARGET_FRAMEWORK:-net10.0}"
configuration="${GRAPHIFY_CSHARP_WATCH_E2E_CONFIGURATION:-Release}"
package_version="${GRAPHIFY_CSHARP_WATCH_E2E_PACKAGE_VERSION:-0.2.0-watcher-e2e}"
package_path="${GRAPHIFY_CSHARP_WATCH_E2E_PACKAGE_PATH:-}"
watch_scan_interval="${GRAPHIFY_CSHARP_WATCH_E2E_SCAN_INTERVAL:-00:00:01}"
conflict_configuration="${GRAPHIFY_CSHARP_WATCH_E2E_CONFLICT_CONFIGURATION:-}"
if [[ -z "$conflict_configuration" ]]; then
  if [[ "$configuration" == "Debug" ]]; then
    conflict_configuration="Release"
  else
    conflict_configuration="Debug"
  fi
fi
if [[ "$conflict_configuration" == "$configuration" ]]; then
  echo 'The watcher E2E conflict configuration must differ from the watcher configuration.' >&2
  exit 64
fi

temporary_base="${TMPDIR:-/tmp}"
temporary_base="${temporary_base%/}"
temporary_root="$(mktemp -d "$temporary_base/graphify-csharp-watcher-e2e.XXXXXX")"
fixture_root="$temporary_root/fixture"
feed_directory="$temporary_root/feed"
tool_directory="$temporary_root/tool"
build_state_directory="$fixture_root/build-state"
compiled_output_directory="$fixture_root/compiled-output"
restore_state_directory="$fixture_root/restore-state"
output_path="$fixture_root/graphify-out/csharp.json"
alternate_output_path="$fixture_root/graphify-out/alternate.json"
watcher_log="$temporary_root/watcher.log"
watcher_stdout="$temporary_root/watcher.stdout"
client_log="$temporary_root/client.log"
watcher_pid=""
watcher_session_id=""
second_watcher_pid=""
second_watcher_session_id=""
e2e_stage="setup"
state_directory="$temporary_root/state"
second_output_path="$fixture_root/graphify-out/second.json"
second_watcher_log="$temporary_root/second-watcher.log"
second_watcher_stdout="$temporary_root/second-watcher.stdout"
caller_export_root="$temporary_root/caller-export"
default_export_path="$caller_export_root/graphify-out/csharp.json"
mkdir -p "$fixture_root" "$feed_directory" "$tool_directory" "$state_directory"
export GRAPHIFY_CSHARP_STATE_DIR="$state_directory"

cleanup() {
  stop_watcher || true
  stop_second_watcher || true
  rm -rf "$temporary_root"
}

on_exit() {
  local exit_code=$?
  if [[ "$exit_code" -ne 0 ]]; then
    echo "watcher-e2e-failed stage=$e2e_stage exit=$exit_code" >&2
    echo '--- watcher log ---' >&2
    sed -n '1,200p' "$watcher_log" >&2 2>/dev/null || true
    echo '--- watcher stdout ---' >&2
    sed -n '1,200p' "$watcher_stdout" >&2 2>/dev/null || true
    echo '--- second watcher log ---' >&2
    sed -n '1,200p' "$second_watcher_log" >&2 2>/dev/null || true
    echo '--- second watcher stdout ---' >&2
    sed -n '1,200p' "$second_watcher_stdout" >&2 2>/dev/null || true
    echo '--- client log ---' >&2
    sed -n '1,200p' "$client_log" >&2 2>/dev/null || true
    for evidence_path in "$temporary_root/ps.json" "$temporary_root/inspect.json" "$temporary_root/info.json" "$temporary_root/diagnostics-result.json" "$temporary_root/default-export-result.json" "$temporary_root/stop.json" "$temporary_root/noise-before.json" "$temporary_root/noise-after.json"; do
      if [[ -f "$evidence_path" ]]; then
        echo "--- $(basename "$evidence_path") ---" >&2
        sed -n '1,200p' "$evidence_path" >&2 || true
      fi
    done
  fi
  cleanup
  exit "$exit_code"
}
trap on_exit EXIT

cp -R "$repository_root/tests/Fixtures/ReferenceFixture/." "$fixture_root/"
# Keep the fixture copy clean even when the contributor's checkout has local
# build output from another test run.
rm -rf "$fixture_root/bin" "$fixture_root/obj" "$build_state_directory" \
  "$compiled_output_directory" "$restore_state_directory"

mkdir -p "$build_state_directory" "$fixture_root/artifacts" "$fixture_root/bin" \
  "$fixture_root/.e2e" "$fixture_root/graphify-out"
printf '%s\n' \
  'namespace ReferenceFixture.Production;' \
  'public sealed class ExplicitBuildStateGenerated { }' \
  > "$build_state_directory/ExplicitBuildStateGenerated.cs"
printf '%s\n' \
  'namespace ReferenceFixture.Production;' \
  'public sealed class BuildStateNoise { }' \
  > "$build_state_directory/BuildStateNoise.cs"
printf '%s\n' \
  'namespace ReferenceFixture.Production;' \
  'public sealed class ArtifactNamedSource { }' \
  > "$fixture_root/artifacts/ArtifactNamedSource.cs"
printf '%s\n' \
  'namespace ReferenceFixture.Production;' \
  'public sealed class BinNamedSource { }' \
  > "$fixture_root/bin/BinNamedSource.cs"
printf '%s\n' \
  'namespace ReferenceFixture.Production;' \
  'public sealed class E2eNamedSource { }' \
  > "$fixture_root/.e2e/E2eNamedSource.cs"
printf '%s\n' \
  'namespace ReferenceFixture.Production;' \
  'public sealed class GraphifyOutNamedSource { }' \
  > "$fixture_root/graphify-out/GraphifyOutNamedSource.cs"
perl -0pi -e 's#</Project>#  <PropertyGroup>\n    <BaseOutputPath>compiled-output/</BaseOutputPath>\n    <BaseIntermediateOutputPath>build-state/</BaseIntermediateOutputPath>\n    <MSBuildProjectExtensionsPath>restore-state/</MSBuildProjectExtensionsPath>\n    <ProjectAssetsFile>restore-state/project.assets.json</ProjectAssetsFile>\n  </PropertyGroup>\n  <ItemGroup>\n    <Compile Remove="build-state/**/*.cs" />\n    <Compile Remove="artifacts/**/*.cs" />\n    <Compile Remove="bin/**/*.cs" />\n    <Compile Remove=".e2e/**/*.cs" />\n    <Compile Remove="graphify-out/**/*.cs" />\n    <Compile Include="build-state/ExplicitBuildStateGenerated.cs" />\n    <Compile Include="artifacts/ArtifactNamedSource.cs" />\n    <Compile Include="bin/BinNamedSource.cs" />\n    <Compile Include=".e2e/E2eNamedSource.cs" />\n    <Compile Include="graphify-out/GraphifyOutNamedSource.cs" />\n  </ItemGroup>\n</Project>#' \
  "$fixture_root/ReferenceFixture.csproj"

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

run_instance_refresh() {
  local session_id="$1"
  shift
  "$tool_directory/graphify-csharp" \
    refresh \
    --instance "$session_id" \
    "$@"
}

export_instance() {
  local session_id="$1"
  local requested_output_path="$2"
  "$tool_directory/graphify-csharp" \
    export \
    --instance "$session_id" \
    -o "$requested_output_path"
}

run_tool() {
  run_instance_refresh "$watcher_session_id" "$@"
  export_instance "$watcher_session_id" "$output_path"
}

run_disk_export() {
  local requested_output_path="$1"
  local requested_configuration="$2"
  shift 2
  "$tool_directory/graphify-csharp" \
    export \
    --input "$fixture_root/ReferenceFixture.csproj" \
    --root "$fixture_root" \
    --configuration "$requested_configuration" \
    --target-framework "$target_framework" \
    --output "$requested_output_path" \
    "$@"
}

resolve_session_id() {
  local requested_input_path="$1"
  local excluded_session_id="${2:-}"
  for _ in {1..30}; do
    local session_id
    session_id="$($tool_directory/graphify-csharp ps --json \
      | jq -r --arg input "$requested_input_path" --arg excluded "$excluded_session_id" \
        '.sessions[] | select(.input_path == $input and .output_path == null and .reachability == "reachable" and .session_id != $excluded) | .session_id' \
      | head -n 1)"
    if [[ -n "$session_id" && "$session_id" != "null" ]]; then
      printf '%s\n' "$session_id"
      return
    fi
    sleep 1
  done
  echo "Could not resolve the output-free watcher session for '$requested_input_path'." >&2
  return 1
}

wait_for_watcher_ready() {
  local session_id="$1"
  local readiness_path="$temporary_root/readiness.json"
  for _ in {1..120}; do
    if "$tool_directory/graphify-csharp" inspect "$session_id" --json > "$readiness_path" 2>/dev/null \
      && jq -e '.success == true and .inspection.ready == true' "$readiness_path" >/dev/null; then
      return
    fi
    sleep 1
  done

  sed -n '1,160p' "$readiness_path" >&2 2>/dev/null || true
  echo "Watcher session '$session_id' did not become ready within two minutes." >&2
  return 1
}

wait_for_watcher_quiescent() {
  local session_id="$1"
  local quiescence_path="$temporary_root/quiescence.json"
  for _ in {1..120}; do
    if "$tool_directory/graphify-csharp" inspect "$session_id" --json > "$quiescence_path" 2>/dev/null \
      && jq -e \
        '.success == true
         and .inspection.ready == true
         and .inspection.lifecycle_state == "ready"
         and .inspection.event_generation == .inspection.indexed_generation' \
        "$quiescence_path" >/dev/null; then
      return
    fi
    sleep 1
  done

  sed -n '1,160p' "$quiescence_path" >&2 2>/dev/null || true
  echo "Watcher session '$session_id' did not become quiescent within two minutes." >&2
  return 1
}

start_watcher_instance() {
  local requested_log_path="$1"
  local role="$2"
  local requested_stdout_path
  local excluded_session_id=""
  if [[ "$role" == "first" ]]; then
    requested_stdout_path="$watcher_stdout"
  else
    requested_stdout_path="$second_watcher_stdout"
    excluded_session_id="$watcher_session_id"
  fi
  : > "$requested_log_path"
  : > "$requested_stdout_path"
  "$tool_directory/graphify-csharp" \
    watch \
    --input "$fixture_root/ReferenceFixture.csproj" \
    --root "$fixture_root" \
    --configuration "$configuration" \
    --target-framework "$target_framework" \
    --watch-scan-interval "$watch_scan_interval" \
    > "$requested_stdout_path" 2> "$requested_log_path" &
  local started_pid=$!
  if [[ "$role" == "first" ]]; then
    watcher_pid="$started_pid"
    watcher_session_id=""
  else
    second_watcher_pid="$started_pid"
    second_watcher_session_id=""
  fi
  for _ in {1..120}; do
    if grep -Fq 'Watching ' "$requested_stdout_path"; then
      local resolved_session_id
      resolved_session_id="$(resolve_session_id "$fixture_root/ReferenceFixture.csproj" "$excluded_session_id")"
      if [[ "$role" == "first" ]]; then
        watcher_session_id="$resolved_session_id"
      else
        second_watcher_session_id="$resolved_session_id"
      fi
      wait_for_watcher_ready "$resolved_session_id"
      for _ in {1..20}; do
        if grep -Fq 'graphify-csharp: Starting;' "$requested_log_path" \
          && grep -Fq 'graphify-csharp: Ready;' "$requested_log_path"; then
          break
        fi
        sleep 0.1
      done
      grep -Fq 'graphify-csharp: Starting;' "$requested_log_path"
      grep -Fq 'session=' "$requested_log_path"
      grep -Fq "input=$fixture_root/ReferenceFixture.csproj" "$requested_log_path"
      grep -Fq 'graphify-csharp: Ready;' "$requested_log_path"
      starting_line="$(grep -n -m 1 -F 'graphify-csharp: Starting;' "$requested_log_path" | cut -d: -f1)"
      ready_line="$(grep -n -m 1 -F 'graphify-csharp: Ready;' "$requested_log_path" | cut -d: -f1)"
      test "$starting_line" -lt "$ready_line"
      return
    fi

    if ! kill -0 "$started_pid" 2>/dev/null; then
      sed -n '1,160p' "$requested_log_path" >&2 || true
      echo 'Watcher exited before becoming ready.' >&2
      exit 1
    fi

    sleep 1
  done

  sed -n '1,160p' "$requested_log_path" >&2 || true
  echo 'Watcher did not become ready within two minutes.' >&2
  exit 1
}

start_watcher() {
  start_watcher_instance "$watcher_log" first
}

start_second_watcher() {
  start_watcher_instance "$second_watcher_log" second
}

stop_watcher() {
  if [[ -n "$watcher_pid" ]]; then
    if kill -0 "$watcher_pid" 2>/dev/null && [[ -n "$watcher_session_id" ]]; then
      "$tool_directory/graphify-csharp" stop "$watcher_session_id" --json > "$client_log" 2>&1 || true
    fi
    for _ in {1..60}; do
      if ! kill -0 "$watcher_pid" 2>/dev/null; then
        break
      fi
      sleep 0.5
    done
    # A task-owned worker that failed before management became available is
    # still cleaned up by its captured process handle; normal shutdown above
    # is always exercised through the production stop command.
    if kill -0 "$watcher_pid" 2>/dev/null; then
      kill "$watcher_pid" 2>/dev/null || true
    fi
    wait "$watcher_pid" 2>/dev/null || true
    watcher_pid=""
    watcher_session_id=""
  fi
}

stop_second_watcher() {
  if [[ -n "$second_watcher_pid" ]]; then
    if kill -0 "$second_watcher_pid" 2>/dev/null && [[ -n "$second_watcher_session_id" ]]; then
      "$tool_directory/graphify-csharp" stop "$second_watcher_session_id" --json > "$client_log" 2>&1 || true
    fi
    for _ in {1..60}; do
      if ! kill -0 "$second_watcher_pid" 2>/dev/null; then
        break
      fi
      sleep 0.5
    done
    if kill -0 "$second_watcher_pid" 2>/dev/null; then
      kill "$second_watcher_pid" 2>/dev/null || true
    fi
    wait "$second_watcher_pid" 2>/dev/null || true
    second_watcher_pid=""
    second_watcher_session_id=""
  fi
}

e2e_stage="initial matching refresh"
start_watcher
run_tool > "$client_log"
grep -Fq 'refreshed (' "$client_log"
grep -Fq 'exported ' "$client_log"
jq -e '.nodes | length > 0' "$output_path" >/dev/null
jq -e '.edges | length > 0' "$output_path" >/dev/null
grep -Fq 'ReferenceFixture.Production.ExplicitBuildStateGenerated' "$output_path"
for source_label in \
  'ReferenceFixture.Production.ArtifactNamedSource' \
  'ReferenceFixture.Production.BinNamedSource' \
  'ReferenceFixture.Production.E2eNamedSource' \
  'ReferenceFixture.Production.GraphifyOutNamedSource'; do
  grep -Fq "$source_label" "$output_path"
done

e2e_stage="independent disk export while watcher is active"
cp "$output_path" "$temporary_root/before-change.json"
disk_export_result="$temporary_root/disk-export.json"
run_disk_export "$alternate_output_path" "$conflict_configuration" --json > "$disk_export_result"
jq -e \
  '.success == true and .mode == "disk" and .session_id == null and .export.output_path != null' \
  "$disk_export_result" >/dev/null
test -s "$alternate_output_path"
cmp -s "$temporary_root/before-change.json" "$output_path"

e2e_stage="alternate instance export isolation"
mkdir -p "$caller_export_root"
default_export_result="$temporary_root/default-export-result.json"
(
  cd -- "$caller_export_root"
  "$tool_directory/graphify-csharp" export --instance "$watcher_session_id" --json
) > "$default_export_result"
canonical_default_export_path="$(realpath "$default_export_path")"
jq -e --arg output "$canonical_default_export_path" \
  '.success == true and .export.output_path == $output and .export.nodes > 0 and .export.edges > 0' \
  "$default_export_result" >/dev/null
test -s "$default_export_path"

export_instance "$watcher_session_id" "$alternate_output_path" > "$client_log"
grep -Fq 'exported ' "$client_log"
test -s "$alternate_output_path"
cmp -s "$temporary_root/before-change.json" "$output_path"

e2e_stage="ignored evaluated build-state noise"
wait_for_watcher_quiescent "$watcher_session_id"
"$tool_directory/graphify-csharp" inspect "$watcher_session_id" --json > "$temporary_root/noise-before.json"
noise_before_event_generation="$(jq -r '.inspection.event_generation' "$temporary_root/noise-before.json")"
noise_before_indexed_generation="$(jq -r '.inspection.indexed_generation' "$temporary_root/noise-before.json")"
printf '%s\n' \
  'namespace ReferenceFixture.Production;' \
  'public sealed class IgnoredBuildStateNoise { }' \
  > "$build_state_directory/IgnoredBuildStateNoise.cs"
sleep 1
cmp -s "$temporary_root/before-change.json" "$output_path"
"$tool_directory/graphify-csharp" inspect "$watcher_session_id" --json > "$temporary_root/noise-after.json"
jq -e \
  --argjson event_generation "$noise_before_event_generation" \
  --argjson indexed_generation "$noise_before_indexed_generation" \
  '.inspection.event_generation == $event_generation
   and .inspection.indexed_generation == $indexed_generation' \
  "$temporary_root/noise-after.json" >/dev/null

e2e_stage="explicit generated source refresh"
printf '\npublic sealed class ExplicitBuildStateChange { }\n' >> "$build_state_directory/ExplicitBuildStateGenerated.cs"
run_tool > "$client_log"
grep -Fq 'refreshed (' "$client_log"
grep -Fq 'ReferenceFixture.Production.ExplicitBuildStateChange' "$output_path"

e2e_stage="arbitrary named source refresh"
printf '\npublic sealed class ArtifactNamedSourceChange { }\n' >> "$fixture_root/artifacts/ArtifactNamedSource.cs"
run_tool > "$client_log"
grep -Fq 'refreshed (' "$client_log"
grep -Fq 'ReferenceFixture.Production.ArtifactNamedSourceChange' "$output_path"

e2e_stage="future membership reconciliation"
printf '%s\n' \
  'namespace ReferenceFixture.Production;' \
  'public sealed class FutureBuildStateSource { }' \
  > "$build_state_directory/FutureBuildStateSource.cs"
run_tool > "$client_log"
if grep -Fq 'ReferenceFixture.Production.FutureBuildStateSource' "$output_path"; then
  echo 'A new unlisted build-state source was indexed without an MSBuild membership change.' >&2
  exit 1
fi

e2e_stage="membership addition"
perl -0pi -e 's#<Compile Include="build-state/ExplicitBuildStateGenerated.cs" />#<Compile Include="build-state/ExplicitBuildStateGenerated.cs" />\n    <Compile Include="build-state/FutureBuildStateSource.cs" />#' \
  "$fixture_root/ReferenceFixture.csproj"
run_tool > "$client_log"
grep -Fq 'ReferenceFixture.Production.FutureBuildStateSource' "$output_path"

e2e_stage="custom restore metadata refresh"
"$tool_directory/graphify-csharp" inspect "$watcher_session_id" --json > "$temporary_root/restore-before.json"
restore_before_event_generation="$(jq -r '.inspection.event_generation' "$temporary_root/restore-before.json")"
printf '\n' >> "$restore_state_directory/project.assets.json"
for _ in {1..30}; do
  "$tool_directory/graphify-csharp" inspect "$watcher_session_id" --json > "$temporary_root/restore-after.json"
  if jq -e \
    --argjson before_event_generation "$restore_before_event_generation" \
    '.inspection.event_generation > $before_event_generation' \
    "$temporary_root/restore-after.json" >/dev/null; then
    break
  fi
  sleep 1
done
run_tool > "$client_log"
grep -Fq 'refreshed (' "$client_log"
"$tool_directory/graphify-csharp" inspect "$watcher_session_id" --json > "$temporary_root/restore-after.json"
jq -e \
  --argjson before_event_generation "$restore_before_event_generation" \
  '.inspection.event_generation > $before_event_generation' \
  "$temporary_root/restore-after.json" >/dev/null

e2e_stage="membership removal"
perl -0pi -e 's#\s*<Compile Include="build-state/ExplicitBuildStateGenerated.cs" />##' \
  "$fixture_root/ReferenceFixture.csproj"
run_tool > "$client_log"
if grep -Fq 'ReferenceFixture.Production.ExplicitBuildStateGenerated' "$output_path"; then
  echo 'A source removed from the evaluated project remained in the graph.' >&2
  exit 1
fi

e2e_stage="cold rebuild comparison"
fresh_output_path="$temporary_root/fresh.json"
run_disk_export "$fresh_output_path" "$configuration" --rebuild > "$client_log"
cmp -s "$fresh_output_path" "$output_path"

e2e_stage="backup inventory refresh"
cp "$output_path" "$temporary_root/before-backup-change.json"
printf '\npublic sealed class BackupAndWarmRefreshChange { }\n' >> "$fixture_root/ReferenceTypes.cs"
sleep 2
if ! cmp -s "$temporary_root/before-backup-change.json" "$output_path"; then
  echo 'The watcher published a source change before the explicit refresh barrier.' >&2
  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$temporary_root/before-backup-change.json" "$output_path" >&2 || true
  fi
  diff -u "$temporary_root/before-backup-change.json" "$output_path" | sed -n '1,120p' >&2 || true
  exit 1
fi

run_tool > "$client_log"
grep -Fq 'refreshed (' "$client_log"
grep -Fq 'ReferenceFixture.Production.BackupAndWarmRefreshChange' "$output_path"

e2e_stage="watcher restart recovery"
stop_watcher
printf '\npublic sealed class RestartRecoveryChange { }\n' >> "$fixture_root/ReferenceTypes.cs"
start_watcher
run_tool > "$client_log"
grep -Fq 'refreshed (' "$client_log"
grep -Fq 'ReferenceFixture.Production.RestartRecoveryChange' "$output_path"

e2e_stage="final watcher rebuild"
run_tool --rebuild > "$client_log"
grep -Fq 'refreshed (' "$client_log"
jq -e '.nodes | length > 0' "$output_path" >/dev/null
jq -e '.edges | length > 0' "$output_path" >/dev/null

e2e_stage="management list and inspect"
start_second_watcher
management_ps_path="$temporary_root/ps.json"
"$tool_directory/graphify-csharp" ps --json > "$management_ps_path"
jq -e --arg first "$watcher_session_id" --arg second "$second_watcher_session_id" \
  '[.sessions[] | .session_id] | index($first) != null and index($second) != null' \
  "$management_ps_path" >/dev/null
"$tool_directory/graphify-csharp" inspect "$watcher_session_id" --json > "$temporary_root/inspect.json"
jq -e --arg session "$watcher_session_id" \
  '.success == true and .inspection.session_id == $session and (.inspection.lifecycle_state == "ready" or .inspection.lifecycle_state == "refreshing" or .inspection.lifecycle_state == "recovering")' \
  "$temporary_root/inspect.json" >/dev/null

e2e_stage="management info and diagnostics"
"$tool_directory/graphify-csharp" info "$watcher_session_id" --json > "$temporary_root/info.json"
jq -e --arg session "$watcher_session_id" \
  '.success == true
   and .command == "inspect"
   and .inspection.session_id == $session
   and .inspection.observation.evidence.projects == 1
   and (.inspection.observation.resources.working_set_bytes == null
        or .inspection.observation.resources.working_set_bytes >= 0)' \
  "$temporary_root/info.json" >/dev/null
diagnostics_path="$temporary_root/diagnostics.json"
"$tool_directory/graphify-csharp" diagnostics "$watcher_session_id" \
  --output "$diagnostics_path" --json > "$temporary_root/diagnostics-result.json"
jq -e --arg path "$diagnostics_path" \
  '.success == true and .report_path == $path' "$temporary_root/diagnostics-result.json" >/dev/null
jq -e --arg session "$watcher_session_id" \
  '.schema_version == "graphify-csharp/diagnostics/v1"
   and .inspection.session_id == $session
   and .inspection.output_path == null
   and .observation.evidence.projects == 1
   and (.observation.recent_events | length) <= 32
   and (.runtime.effective_processor_count > 0)' "$diagnostics_path" >/dev/null
test "$(wc -c < "$diagnostics_path" | tr -d ' ')" -le 65536

e2e_stage="management stop isolation"
"$tool_directory/graphify-csharp" stop "$watcher_session_id" --json > "$temporary_root/stop.json"
jq -e --arg session "$watcher_session_id" \
  '.success == true and .inspection.session_id == $session and .inspection.lifecycle_state == "stopped"' \
  "$temporary_root/stop.json" >/dev/null
stopped_session_id="$watcher_session_id"
for _ in {1..60}; do
  if ! kill -0 "$watcher_pid" 2>/dev/null; then
    break
  fi
  sleep 0.5
done
if kill -0 "$watcher_pid" 2>/dev/null; then
  echo 'The production stop command returned but the first watcher remained alive.' >&2
  exit 1
fi
wait "$watcher_pid" 2>/dev/null || true
watcher_pid=""
watcher_session_id=""
"$tool_directory/graphify-csharp" ps --json > "$management_ps_path"
jq -e --arg stopped "$stopped_session_id" --arg second "$second_watcher_session_id" \
  '[.sessions[] | .session_id] | index($stopped) == null and index($second) != null' \
  "$management_ps_path" >/dev/null
export_instance "$second_watcher_session_id" "$second_output_path" > "$client_log"
grep -Fq 'exported ' "$client_log"
test -s "$second_output_path"

e2e_stage="stale session and replacement isolation"
crashed_session_id="$second_watcher_session_id"
kill -9 "$second_watcher_pid" 2>/dev/null || true
wait "$second_watcher_pid" 2>/dev/null || true
second_watcher_pid=""
second_watcher_session_id=""
"$tool_directory/graphify-csharp" ps --json > "$management_ps_path"
jq -e --arg crashed "$crashed_session_id" \
  'any(.sessions[]; .session_id == $crashed and .reachability == "stale")' \
  "$management_ps_path" >/dev/null

start_second_watcher
replacement_session_id="$second_watcher_session_id"
if [[ "$replacement_session_id" == "$crashed_session_id" ]]; then
  echo 'A replacement watcher reused the crashed session ID.' >&2
  exit 1
fi
"$tool_directory/graphify-csharp" ps --json > "$management_ps_path"
jq -e --arg crashed "$crashed_session_id" --arg replacement "$replacement_session_id" \
  'any(.sessions[]; .session_id == $crashed and .reachability == "stale")
   and any(.sessions[]; .session_id == $replacement and .reachability == "reachable")' \
  "$management_ps_path" >/dev/null
set +e
"$tool_directory/graphify-csharp" stop "$crashed_session_id" --json > "$temporary_root/stale-stop.json" 2>&1
stale_stop_status=$?
set -e
if [[ "$stale_stop_status" -ne 1 ]]; then
  echo 'Stopping a stale session did not fail safely.' >&2
  exit 1
fi
jq -e '.success == false and .error_code == "stale_session"' \
  "$temporary_root/stale-stop.json" >/dev/null
export_instance "$second_watcher_session_id" "$second_output_path" > "$client_log"
grep -Fq 'exported ' "$client_log"
test -s "$second_output_path"
stop_second_watcher


node_count="$(jq '.nodes | length' "$output_path")"
edge_count="$(jq '.edges | length' "$output_path")"
echo "watcher-e2e-ok framework=$tool_framework package=$package_version nodes=$node_count edges=$edge_count"

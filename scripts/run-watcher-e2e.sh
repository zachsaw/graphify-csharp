#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
tool_framework="${GRAPHIFY_CSHARP_WATCH_E2E_FRAMEWORK:-net10.0}"
target_framework="${GRAPHIFY_CSHARP_WATCH_E2E_TARGET_FRAMEWORK:-net10.0}"
configuration="${GRAPHIFY_CSHARP_WATCH_E2E_CONFIGURATION:-Release}"
package_version="${GRAPHIFY_CSHARP_WATCH_E2E_PACKAGE_VERSION:-0.1.0-watcher-e2e}"
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
output_path="$fixture_root/graphify-out/csharp.json"
alternate_output_path="$fixture_root/graphify-out/alternate.json"
watcher_log="$temporary_root/watcher.log"
client_log="$temporary_root/client.log"
watcher_pid=""
watcher_session_id=""
second_watcher_pid=""
second_watcher_session_id=""
e2e_stage="setup"
state_directory="$temporary_root/state"
second_output_path="$fixture_root/graphify-out/second.json"
second_watcher_log="$temporary_root/second-watcher.log"
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
    echo '--- second watcher log ---' >&2
    sed -n '1,200p' "$second_watcher_log" >&2 2>/dev/null || true
    echo '--- client log ---' >&2
    sed -n '1,200p' "$client_log" >&2 2>/dev/null || true
    for evidence_path in "$temporary_root/ps.json" "$temporary_root/inspect.json" "$temporary_root/stop.json"; do
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
rm -rf "$fixture_root/bin" "$fixture_root/obj"

mkdir -p "$fixture_root/obj"
printf '%s\n' \
  'namespace ReferenceFixture.Production;' \
  'public sealed class ExplicitObjGenerated { }' \
  > "$fixture_root/obj/ExplicitObjGenerated.cs"
printf '%s\n' \
  'namespace ReferenceFixture.Production;' \
  'public sealed class ObjNoise { }' \
  > "$fixture_root/obj/ObjNoise.cs"
perl -0pi -e 's#</Project>#  <ItemGroup>\n    <Compile Include="obj/ExplicitObjGenerated.cs" />\n  </ItemGroup>\n</Project>#' \
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

run_tool_at_output_for_configuration() {
  local requested_output_path="$1"
  local requested_configuration="$2"
  shift 2
  "$tool_directory/graphify-csharp" \
    --input "$fixture_root/ReferenceFixture.csproj" \
    --root "$fixture_root" \
    --configuration "$requested_configuration" \
    --target-framework "$target_framework" \
    --output "$requested_output_path" \
    "$@"
}

run_tool() {
  run_tool_at_output "$output_path" "$@"
}

resolve_session_id() {
  local requested_output_path="$1"
  for _ in {1..30}; do
    local session_id
    session_id="$($tool_directory/graphify-csharp ps --json \
      | jq -r --arg output "$requested_output_path" \
        '.sessions[] | select(.output_path == $output and .reachability == "reachable") | .session_id' \
      | head -n 1)"
    if [[ -n "$session_id" && "$session_id" != "null" ]]; then
      printf '%s\n' "$session_id"
      return
    fi
    sleep 1
  done
  echo "Could not resolve the management session for '$requested_output_path'." >&2
  return 1
}

start_watcher_instance() {
  local requested_output_path="$1"
  local requested_log_path="$2"
  local role="$3"
  : > "$requested_log_path"
  "$tool_directory/graphify-csharp" \
    --input "$fixture_root/ReferenceFixture.csproj" \
    --root "$fixture_root" \
    --configuration "$configuration" \
    --target-framework "$target_framework" \
    --output "$requested_output_path" \
    --watch \
    --watch-scan-interval "$watch_scan_interval" > "$requested_log_path" 2>&1 &
  local started_pid=$!
  if [[ "$role" == "first" ]]; then
    watcher_pid="$started_pid"
    watcher_session_id=""
  else
    second_watcher_pid="$started_pid"
    second_watcher_session_id=""
  fi
  for _ in {1..120}; do
    if [[ -s "$requested_output_path" ]] && grep -Fq 'Watching ' "$requested_log_path"; then
      local resolved_session_id
      resolved_session_id="$(resolve_session_id "$requested_output_path")"
      if [[ "$role" == "first" ]]; then
        watcher_session_id="$resolved_session_id"
      else
        second_watcher_session_id="$resolved_session_id"
      fi
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
  start_watcher_instance "$output_path" "$watcher_log" first
}

start_second_watcher() {
  start_watcher_instance "$second_output_path" "$second_watcher_log" second
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
grep -Fq '(watcher,' "$client_log"
jq -e '.nodes | length > 0' "$output_path" >/dev/null
jq -e '.edges | length > 0' "$output_path" >/dev/null
grep -Fq 'ReferenceFixture.Production.ExplicitObjGenerated' "$output_path"

cp "$output_path" "$temporary_root/before-configuration-conflict.json"
cache_path="$(find "$fixture_root/graphify-out/.graphify-csharp" -maxdepth 1 -type f -name 'manifest-*.json' -print -quit)"
test -n "$cache_path"
cp "$cache_path" "$temporary_root/before-configuration-conflict.cache.json"
set +e
e2e_stage="same-output configuration conflict"
run_tool_at_output_for_configuration "$output_path" "$conflict_configuration" > "$client_log" 2>&1
configuration_conflict_status=$?
set -e
if [[ "$configuration_conflict_status" -ne 1 ]]; then
  sed -n '1,160p' "$client_log" >&2 || true
  echo 'A different configuration did not fail when the canonical output was owned by the watcher.' >&2
  exit 1
fi
grep -Fq 'already owned' "$client_log"
cmp -s "$temporary_root/before-configuration-conflict.json" "$output_path"
cmp -s "$temporary_root/before-configuration-conflict.cache.json" "$cache_path"

case_alias_output_path="$fixture_root/graphify-out/CSHARP.JSON"
if [[ -e "$case_alias_output_path" ]]; then
  e2e_stage="case-equivalent output conflict"
  cp "$output_path" "$temporary_root/before-case-alias-conflict.json"
  set +e
  run_tool_at_output_for_configuration "$case_alias_output_path" "$conflict_configuration" > "$client_log" 2>&1
  case_alias_status=$?
  set -e
  if [[ "$case_alias_status" -ne 1 ]]; then
    sed -n '1,160p' "$client_log" >&2 || true
    echo 'A case-equivalent output path was not rejected while the canonical watcher was active.' >&2
    exit 1
  fi
  grep -Fq 'already owned' "$client_log"
  cmp -s "$temporary_root/before-case-alias-conflict.json" "$output_path"
fi

e2e_stage="alternate output isolation"
cp "$output_path" "$temporary_root/before-change.json"
run_tool_at_output "$alternate_output_path" > "$client_log"
if grep -Fq '(watcher,' "$client_log"; then
  echo 'An alternate output request incorrectly attached to the canonical watcher.' >&2
  exit 1
fi
test -s "$alternate_output_path"
cmp -s "$temporary_root/before-change.json" "$output_path"

e2e_stage="ignored build-output noise"
printf '%s\n' \
  'namespace ReferenceFixture.Production;' \
  'public sealed class IgnoredObjNoise { }' \
  > "$fixture_root/obj/IgnoredObjNoise.cs"
sleep 1
cmp -s "$temporary_root/before-change.json" "$output_path"

e2e_stage="explicit generated source refresh"
printf '\npublic sealed class ExplicitObjChange { }\n' >> "$fixture_root/obj/ExplicitObjGenerated.cs"
run_tool > "$client_log"
grep -Fq '(watcher,' "$client_log"
grep -Fq 'ReferenceFixture.Production.ExplicitObjChange' "$output_path"

e2e_stage="future membership reconciliation"
printf '%s\n' \
  'namespace ReferenceFixture.Production;' \
  'public sealed class FutureObjSource { }' \
  > "$fixture_root/obj/FutureObjSource.cs"
run_tool > "$client_log"
if grep -Fq 'ReferenceFixture.Production.FutureObjSource' "$output_path"; then
  echo 'A new unlisted obj source was indexed without an MSBuild membership change.' >&2
  exit 1
fi

e2e_stage="membership addition"
perl -0pi -e 's#<Compile Include="obj/ExplicitObjGenerated.cs" />#<Compile Include="obj/ExplicitObjGenerated.cs" />\n    <Compile Include="obj/FutureObjSource.cs" />#' \
  "$fixture_root/ReferenceFixture.csproj"
run_tool > "$client_log"
grep -Fq 'ReferenceFixture.Production.FutureObjSource' "$output_path"

e2e_stage="membership removal"
perl -0pi -e 's#\s*<Compile Include="obj/ExplicitObjGenerated.cs" />##' \
  "$fixture_root/ReferenceFixture.csproj"
run_tool > "$client_log"
if grep -Fq 'ReferenceFixture.Production.ExplicitObjGenerated' "$output_path"; then
  echo 'A source removed from the evaluated project remained in the graph.' >&2
  exit 1
fi

e2e_stage="cold rebuild comparison"
fresh_output_path="$temporary_root/fresh.json"
run_tool_at_output "$fresh_output_path" --rebuild > "$client_log"
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
grep -Fq '(watcher,' "$client_log"
grep -Fq 'ReferenceFixture.Production.BackupAndWarmRefreshChange' "$output_path"

e2e_stage="watcher restart recovery"
stop_watcher
printf '\npublic sealed class RestartRecoveryChange { }\n' >> "$fixture_root/ReferenceTypes.cs"
start_watcher
grep -Fq 'ReferenceFixture.Production.RestartRecoveryChange' "$output_path"

e2e_stage="final watcher rebuild"
run_tool --rebuild > "$client_log"
grep -Fq '(watcher,' "$client_log"
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
run_tool_at_output "$second_output_path" > "$client_log"
grep -Fq '(watcher,' "$client_log"
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
run_tool_at_output "$second_output_path" > "$client_log"
grep -Fq '(watcher,' "$client_log"
test -s "$second_output_path"
stop_second_watcher


node_count="$(jq '.nodes | length' "$output_path")"
edge_count="$(jq '.edges | length' "$output_path")"
echo "watcher-e2e-ok framework=$tool_framework package=$package_version nodes=$node_count edges=$edge_count"

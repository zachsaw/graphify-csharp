#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
tool_framework="${GRAPHIFY_CSHARP_QUERY_E2E_FRAMEWORK:-net10.0}"
target_framework="${GRAPHIFY_CSHARP_QUERY_E2E_TARGET_FRAMEWORK:-net10.0}"
configuration="${GRAPHIFY_CSHARP_QUERY_E2E_CONFIGURATION:-Release}"
package_version="${GRAPHIFY_CSHARP_QUERY_E2E_PACKAGE_VERSION:-0.2.0-query-e2e}"
package_path="${GRAPHIFY_CSHARP_QUERY_E2E_PACKAGE_PATH:-}"

command -v jq >/dev/null || {
  echo 'run-query-e2e.sh requires jq.' >&2
  exit 64
}

temporary_base="${TMPDIR:-/tmp}"
temporary_base="${temporary_base%/}"
temporary_root="$(mktemp -d "$temporary_base/graphify-csharp-query-e2e.XXXXXX")"
feed_directory="$temporary_root/feed"
tool_directory="$temporary_root/tool"
fixture_a="$temporary_root/fixture-a"
fixture_b="$temporary_root/fixture-b"
state_directory="$temporary_root/state"
log_a="$temporary_root/watcher-a.log"
log_b="$temporary_root/watcher-b.log"
stdout_a="$temporary_root/watcher-a.stdout"
stdout_b="$temporary_root/watcher-b.stdout"
mkdir -p "$feed_directory" "$tool_directory" "$fixture_a" "$fixture_b" "$state_directory"
export GRAPHIFY_CSHARP_STATE_DIR="$state_directory"

watcher_a_pid=""
watcher_b_pid=""
watcher_a_session=""
watcher_b_session=""
stage="setup"

cleanup_session() {
  local session_id="$1"
  if [[ -n "$session_id" ]]; then
    "$tool_directory/graphify-csharp" stop "$session_id" --json >/dev/null 2>&1 || true
  fi
}

cleanup() {
  cleanup_session "$watcher_a_session"
  cleanup_session "$watcher_b_session"
  if [[ -n "$watcher_a_pid" ]]; then
    kill "$watcher_a_pid" >/dev/null 2>&1 || true
  fi
  if [[ -n "$watcher_b_pid" ]]; then
    kill "$watcher_b_pid" >/dev/null 2>&1 || true
  fi
  rm -rf "$temporary_root"
}

on_exit() {
  local exit_code=$?
  if [[ "$exit_code" -ne 0 ]]; then
    echo "query-e2e-failed stage=$stage exit=$exit_code" >&2
    for log_path in "$log_a" "$log_b" "$stdout_a" "$stdout_b"; do
      if [[ -f "$log_path" ]]; then
        echo "--- $(basename -- "$log_path") ---" >&2
        sed -n '1,200p' "$log_path" >&2 || true
      fi
    done
    for evidence_path in "$temporary_root/ps.json" "$temporary_root/startup-info.json" "$temporary_root/inspect-${watcher_a_session}.json" "$temporary_root/inspect-${watcher_b_session}.json"; do
      if [[ -f "$evidence_path" ]]; then
        echo "--- $(basename -- "$evidence_path") ---" >&2
        sed -n '1,200p' "$evidence_path" >&2 || true
      fi
    done
    for evidence_path in "$temporary_root"/*.json; do
      if [[ -f "$evidence_path" && "$evidence_path" != "$temporary_root/ps.json" && "$evidence_path" != "$temporary_root/startup-info.json" && "$evidence_path" != "$temporary_root/inspect-${watcher_a_session}.json" && "$evidence_path" != "$temporary_root/inspect-${watcher_b_session}.json" ]]; then
        echo "--- $(basename -- "$evidence_path") ---" >&2
        sed -n '1,200p' "$evidence_path" >&2 || true
      fi
    done
  fi
  cleanup
  exit "$exit_code"
}
trap on_exit EXIT

prepare_fixture() {
  local destination="$1"
  cp -R "$repository_root/tests/Fixtures/ReferenceFixture/." "$destination/"
  rm -rf "$destination/bin" "$destination/obj" "$destination/graphify-out"
  dotnet restore "$destination/ReferenceFixture.csproj" --nologo
}

prepare_fixture "$fixture_a"
prepare_fixture "$fixture_b"

stage="package"
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
  dotnet restore "$repository_root/Graphify.CSharp.sln" --nologo
  dotnet build "$repository_root/src/Graphify.CSharp.Cli/Graphify.CSharp.Cli.csproj" \
    --configuration "$configuration" \
    --framework "$tool_framework" \
    --no-restore
  dotnet pack "$repository_root/src/Graphify.CSharp.Cli/Graphify.CSharp.Cli.csproj" \
    --configuration "$configuration" \
    --no-build \
    --no-restore \
    --output "$feed_directory" \
    -p:Version="$package_version" \
    -p:PackageVersion="$package_version"
fi

test -f "$feed_directory/Graphify.CSharp.${package_version}.nupkg"
dotnet tool install \
  --tool-path "$tool_directory" \
  --add-source "$feed_directory" \
  --ignore-failed-sources \
  --no-cache \
  --framework "$tool_framework" \
  Graphify.CSharp \
  --version "$package_version"

start_watcher() {
  local fixture_root="$1"
  local log_path="$2"
  local role="$3"
  local stdout_path
  if [[ "$role" == "a" ]]; then
    stdout_path="$stdout_a"
  else
    stdout_path="$stdout_b"
  fi
  : > "$log_path"
  : > "$stdout_path"
  "$tool_directory/graphify-csharp" \
    --input "$fixture_root/ReferenceFixture.csproj" \
    --root "$fixture_root" \
    --configuration "$configuration" \
    --target-framework "$target_framework" \
    --watch > "$stdout_path" 2> "$log_path" &
  local started_pid=$!
  if [[ "$role" == "a" ]]; then
    watcher_a_pid="$started_pid"
  else
    watcher_b_pid="$started_pid"
  fi
}

resolve_session() {
  local input_path="$1"
  local session_json="$temporary_root/ps.json"
  for _ in {1..120}; do
    if "$tool_directory/graphify-csharp" ps --json > "$session_json" 2>/dev/null; then
      local session_id
      session_id="$(jq -r --arg input "$input_path" \
        '.sessions[] | select(.input_path == $input and .output_path == null and .reachability == "reachable") | .session_id' \
        "$session_json" | head -n 1)"
      if [[ -n "$session_id" && "$session_id" != "null" ]]; then
        printf '%s\n' "$session_id"
        return 0
      fi
    fi
    sleep 0.25
  done
  cat "$session_json" >&2 2>/dev/null || true
  echo "Could not resolve a query-only session for '$input_path'." >&2
  return 1
}

wait_ready() {
  local session_id="$1"
  local inspect_json="$temporary_root/inspect-${session_id}.json"
  for _ in {1..240}; do
    if "$tool_directory/graphify-csharp" inspect "$session_id" --json > "$inspect_json" 2>/dev/null \
      && jq -e '.success == true and .inspection.ready == true and .inspection.output_path == null' "$inspect_json" >/dev/null; then
      return 0
    fi
    sleep 0.25
  done
  cat "$inspect_json" >&2 2>/dev/null || true
  echo "Session '$session_id' did not become ready." >&2
  return 1
}

wait_for_progress_line() {
  local log_path="$1"
  local expected_line="$2"
  for _ in {1..120}; do
    if grep -Fq "$expected_line" "$log_path"; then
      return 0
    fi
    sleep 0.25
  done
  echo "Timed out waiting for '$expected_line' in '$log_path'." >&2
  return 1
}

query_instance() {
  local session_id="$1"
  local output_path="$2"
  shift 2
  "$tool_directory/graphify-csharp" query "$@" --instance "$session_id" --json > "$output_path"
}

expect_failure() {
  local output_path="$1"
  shift
  set +e
  "$tool_directory/graphify-csharp" "$@" > "$output_path" 2>/dev/null
  local status=$?
  set -e
  test "$status" -ne 0
}

stage="startup"
start_watcher "$fixture_a" "$log_a" a
start_watcher "$fixture_b" "$log_b" b
watcher_a_session="$(resolve_session "$fixture_a/ReferenceFixture.csproj")"
watcher_b_session="$(resolve_session "$fixture_b/ReferenceFixture.csproj")"
test "$watcher_a_session" != "$watcher_b_session"

# The listener is published before readiness. Issue an info request as soon as
# the descriptor is visible; deterministic startup interleavings are covered
# by the in-process gate tests, while this installed test exercises real IPC.
startup_info="$temporary_root/startup-info.json"
"$tool_directory/graphify-csharp" info "$watcher_a_session" --json > "$startup_info"
jq -e --arg session "$watcher_a_session" \
  '.success == true and .inspection.session_id == $session' \
  "$startup_info" >/dev/null
wait_ready "$watcher_a_session"
wait_ready "$watcher_b_session"

# The foreground watcher is deliberately machine-readable on stdout and
# human-oriented on stderr. The initial notice must precede the final ready
# summary even when the fixture is too small to expose an intermediate tick.
for progress_log in "$log_a" "$log_b"; do
  wait_for_progress_line "$progress_log" 'graphify-csharp: Starting;'
  wait_for_progress_line "$progress_log" 'graphify-csharp: Ready;'
  starting_line="$(grep -n -m 1 -F 'graphify-csharp: Starting;' "$progress_log" | cut -d: -f1)"
  ready_line="$(grep -n -m 1 -F 'graphify-csharp: Ready;' "$progress_log" | cut -d: -f1)"
  test "$starting_line" -lt "$ready_line"
done
grep -Fq 'semantic queries are available' "$stdout_a"
grep -Fq 'semantic queries are available' "$stdout_b"

stage="info-and-diagnostics"
info_before_query="$temporary_root/info-before-query.json"
"$tool_directory/graphify-csharp" info "$watcher_a_session" --json > "$info_before_query"
jq -e --arg session "$watcher_a_session" \
  '.success == true
   and .command == "inspect"
   and .inspection.session_id == $session
   and .inspection.output_path == null
   and .inspection.observation.evidence.projects == 1
   and .inspection.observation.evidence.semantic_index_built == false' \
  "$info_before_query" >/dev/null
test ! -e "$fixture_a/graphify-out/graph.json"

diagnostics_path="$temporary_root/query-diagnostics.json"
diagnostics_result="$temporary_root/query-diagnostics-result.json"
"$tool_directory/graphify-csharp" diagnostics "$watcher_a_session" \
  --output "$diagnostics_path" --json > "$diagnostics_result"
jq -e --arg path "$diagnostics_path" \
  '.success == true and .report_path == $path' "$diagnostics_result" >/dev/null
jq -e --arg session "$watcher_a_session" \
  '.schema_version == "graphify-csharp/diagnostics/v1"
   and .inspection.session_id == $session
   and .observation.evidence.projects == 1
   and (.observation.recent_events | length) <= 32
   and (.runtime.runtime | length) > 0' "$diagnostics_path" >/dev/null
test "$(wc -c < "$diagnostics_path" | tr -d ' ')" -le 65536
diagnostics_digest_before="$(shasum -a 256 "$diagnostics_path" | awk '{print $1}')"
diagnostics_collision="$temporary_root/query-diagnostics-collision.json"
expect_failure "$diagnostics_collision" diagnostics "$watcher_a_session" \
  --output "$diagnostics_path" --json
jq -e '.success == false' "$diagnostics_collision" >/dev/null
diagnostics_digest_after="$(shasum -a 256 "$diagnostics_path" | awk '{print $1}')"
test "$diagnostics_digest_before" = "$diagnostics_digest_after"
test -z "$(find "$temporary_root" -maxdepth 1 -name '.query-diagnostics.json.*.tmp' -print -quit)"

stage="queries"
first_page="$temporary_root/first-page.json"
second_page="$temporary_root/second-page.json"
query_instance "$watcher_a_session" "$first_page" symbols --limit 1
jq -e '.success == true and .page.has_more == true and (.page.next_cursor | type) == "string"' "$first_page" >/dev/null
cursor="$(jq -r '.page.next_cursor' "$first_page")"
query_instance "$watcher_a_session" "$second_page" symbols --limit 1 --cursor "$cursor"
jq -e '.success == true and (.items | length) == 1 and .page.returned == 1' "$second_page" >/dev/null
test "$(jq -r '.items[0].id' "$first_page")" != "$(jq -r '.items[0].id' "$second_page")"

called_symbols="$temporary_root/called-symbols.json"
query_instance "$watcher_a_session" "$called_symbols" symbols Called --kind method --limit 10
jq -e '.success == true and (.items | length) == 2' "$called_symbols" >/dev/null
int_symbol="$(jq -r '.items[] | select(.label | endswith("Called(int)")) | .id' "$called_symbols")"
test -n "$int_symbol"

signature="$temporary_root/signature.json"
callers="$temporary_root/callers.json"
arguments="$temporary_root/arguments.json"
summary="$temporary_root/summary.json"
query_instance "$watcher_a_session" "$signature" signature --symbol "$int_symbol"
query_instance "$watcher_a_session" "$callers" callers --symbol "$int_symbol" --limit 10
query_instance "$watcher_a_session" "$arguments" arguments --symbol "$int_symbol" --limit 10
query_instance "$watcher_a_session" "$summary" usage-summary 'Called(int)' --kind method --limit 10
jq -e '.success == true and .items[0].parameters[0].name == "value"' "$signature" >/dev/null
jq -e '.success == true and any(.items[]; .origin.label | endswith("ProductionCaller.Run")) and any(.items[]; .origin.label | endswith("TestCaller.Run"))' "$callers" >/dev/null
jq -e '.success == true and any(.items[]; .formal_parameter_name == "value")' "$arguments" >/dev/null
jq -e '.success == true and .items[0].calls.edge_count == 3 and .items[0].distinct_origin_count == 3' "$summary" >/dev/null

hierarchy_symbols="$temporary_root/hierarchy-symbols.json"
hierarchy="$temporary_root/hierarchy.json"
query_instance "$watcher_a_session" "$hierarchy_symbols" symbols 'IContract.Execute' --kind method --limit 10
contract_symbol="$(jq -r '.items[0].id' "$hierarchy_symbols")"
query_instance "$watcher_a_session" "$hierarchy" hierarchy --symbol "$contract_symbol" --direction implementations --limit 10
jq -e '.success == true and any(.items[]; .neighbor.label | endswith("Contract.Execute(int)"))' "$hierarchy" >/dev/null

grouped_summary="$temporary_root/grouped-summary.json"
query_instance "$watcher_a_session" "$grouped_summary" usage-summary Unused --kind property --group-by project,namespace --limit 10
jq -e '.success == true and .items[0].calls.edge_count == 0 and .items[0].references.edge_count == 0' "$grouped_summary" >/dev/null

info_after_query="$temporary_root/info-after-query.json"
"$tool_directory/graphify-csharp" info "$watcher_a_session" --json > "$info_after_query"
jq -e \
  '.success == true
   and .inspection.observation.evidence.semantic_index_built == true
   and ((.inspection.observation.evidence.query_caches_built | index("semantic_index")) != null)' \
  "$info_after_query" >/dev/null

stage="edit-and-snapshot"
old_snapshot="$(jq -r '.snapshot.id' "$first_page")"
revision_before_edit="$(jq -r '.inspection.observation.evidence.evidence_revision' "$info_after_query")"
cat >> "$fixture_a/ReferenceTypes.cs" <<'EOF'

public sealed class AddedAfterQueryE2e { }
EOF

# The watcher updates semantic evidence in the background, but it must not
# build the lazy query index merely because info was requested.
info_after_edit="$temporary_root/info-after-edit.json"
for _ in {1..120}; do
  if "$tool_directory/graphify-csharp" info "$watcher_a_session" --json > "$info_after_edit" 2>/dev/null \
    && jq -e --argjson revision "$revision_before_edit" \
      '.success == true
       and .inspection.observation.evidence.evidence_revision > $revision
       and .inspection.observation.evidence.semantic_index_built == false' \
      "$info_after_edit" >/dev/null; then
    break
  fi
  sleep 0.25
done
jq -e --argjson revision "$revision_before_edit" \
  '.success == true
   and .inspection.observation.evidence.evidence_revision > $revision
   and .inspection.observation.evidence.semantic_index_built == false' \
  "$info_after_edit" >/dev/null
test ! -e "$fixture_a/graphify-out/graph.json"

added="$temporary_root/added.json"
for _ in {1..120}; do
  if query_instance "$watcher_a_session" "$added" symbols AddedAfterQueryE2e --limit 10 \
    && jq -e '.success == true and (.items | length) == 1' "$added" >/dev/null; then
    break
  fi
  sleep 0.25
done
jq -e '.success == true and (.items | length) == 1' "$added" >/dev/null

stale_cursor="$temporary_root/stale-cursor.json"
expect_failure "$stale_cursor" query symbols --instance "$watcher_a_session" --limit 1 --cursor "$cursor" --json
jq -e '.success == false and .error.code == "stale_snapshot"' "$stale_cursor" >/dev/null

stale_snapshot="$temporary_root/stale-snapshot.json"
expect_failure "$stale_snapshot" query signature --instance "$watcher_a_session" --symbol "$int_symbol" --snapshot "$old_snapshot" --json
jq -e '.success == false and .error.code == "stale_snapshot"' "$stale_snapshot" >/dev/null

stage="export-and-independence"
export_path="$fixture_a/export.json"
export_result="$temporary_root/export-result.json"
"$tool_directory/graphify-csharp" export --instance "$watcher_a_session" --output "$export_path" --json > "$export_result"
canonical_export_path="$(cd -- "$(dirname -- "$export_path")" && pwd)/$(basename -- "$export_path")"
jq -e --arg output "$canonical_export_path" '.success == true and .export.output_path == $output and .export.nodes > 0 and .export.edges > 0' "$export_result" >/dev/null
export_digest_before="$(shasum -a 256 "$export_path" | awk '{print $1}')"

query_instance "$watcher_b_session" "$temporary_root/b-before.json" symbols AddedAfterQueryE2e --limit 10
jq -e '.success == true and (.items | length) == 0' "$temporary_root/b-before.json" >/dev/null
query_instance "$watcher_a_session" "$temporary_root/a-after-export-edit.json" symbols AddedAfterQueryE2e --limit 10
export_digest_after_edit="$(shasum -a 256 "$export_path" | awk '{print $1}')"
test "$export_digest_before" = "$export_digest_after_edit"

stage="cold-and-stop"
cold_quiet_result="$temporary_root/cold-quiet.json"
cold_quiet_stderr="$temporary_root/cold-quiet.stderr"
"$tool_directory/graphify-csharp" query symbols Called --input "$fixture_b/ReferenceFixture.csproj" \
  --root "$fixture_b" --configuration "$configuration" --target-framework "$target_framework" \
  --limit 10 --no-progress --json > "$cold_quiet_result" 2> "$cold_quiet_stderr"
jq -e '.success == true and .mode == "cold"' "$cold_quiet_result" >/dev/null
test ! -s "$cold_quiet_stderr"

cold_result="$temporary_root/cold.json"
cold_stderr="$temporary_root/cold.stderr"
"$tool_directory/graphify-csharp" query symbols Called --input "$fixture_b/ReferenceFixture.csproj" \
  --root "$fixture_b" --configuration "$configuration" --target-framework "$target_framework" --limit 10 --json > "$cold_result" 2> "$cold_stderr"
jq -e '.success == true and .mode == "cold" and .session_id == null and (.snapshot.id | endswith(":1"))' "$cold_result" >/dev/null
grep -Fq 'graphify-csharp: Starting;' "$cold_stderr"
grep -Fq 'graphify-csharp: Completed;' "$cold_stderr"
test "$(wc -l < "$cold_result" | tr -d ' ')" -gt 0

stopped_session="$watcher_a_session"
cleanup_session "$stopped_session"
watcher_a_session=""
expect_failure "$temporary_root/stopped.json" query symbols --instance "$stopped_session" --limit 1 --json
jq -e '.success == false and .error.code == "session_not_found"' "$temporary_root/stopped.json" >/dev/null
query_instance "$watcher_b_session" "$temporary_root/b-after-a-stop.json" symbols Called --kind method --limit 10
jq -e '.success == true and (.items | length) == 2' "$temporary_root/b-after-a-stop.json" >/dev/null

cleanup_session "$watcher_b_session"
watcher_b_session=""
echo "query-e2e-ok package=$package_version framework=$tool_framework target=$target_framework"

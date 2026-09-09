#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
fixture_root="$repository_root/.e2e/dapper"
output_root="$fixture_root/graphify-out"
repository_url="${GRAPHIFY_CSHARP_E2E_REPOSITORY_URL:-https://github.com/DapperLib/Dapper.git}"
repository_commit="${GRAPHIFY_CSHARP_E2E_REPOSITORY_COMMIT:-6d48ef664acc7298c649e2d449d903b3360d5a90}"
target_framework="${GRAPHIFY_CSHARP_E2E_TARGET_FRAMEWORK:-net10.0}"
configuration="${GRAPHIFY_CSHARP_E2E_CONFIGURATION:-Release}"
package_version="${GRAPHIFY_CSHARP_E2E_PACKAGE_VERSION:-0.1.0-e2e}"

temporary_base="${TMPDIR:-/tmp}"
temporary_base="${temporary_base%/}"
temporary_root="$(mktemp -d "$temporary_base/graphify-csharp-e2e.XXXXXX")"
trap 'rm -rf "$temporary_root"' EXIT
feed_directory="$temporary_root/feed"
package_directory="$temporary_root/package"
tool_directory_net10="$temporary_root/tool-net10"
tool_directory_net11="$temporary_root/tool-net11"
mkdir -p "$feed_directory" "$package_directory" "$tool_directory_net10" "$tool_directory_net11"

dotnet restore "$repository_root/Graphify.CSharp.sln"
dotnet restore "$repository_root/tests/Fixtures/ReferenceFixture/ReferenceFixture.csproj"
dotnet restore "$repository_root/tests/Fixtures/CSharp14Fixture/CSharp14Fixture.csproj"
dotnet restore "$repository_root/tests/Fixtures/CSharp15Fixture/CSharp15Fixture.csproj"
dotnet build "$repository_root/Graphify.CSharp.sln" --configuration "$configuration" --no-restore
dotnet test "$repository_root/Graphify.CSharp.sln" --configuration "$configuration" --no-build --no-restore
dotnet pack "$repository_root/src/Graphify.CSharp.Cli/Graphify.CSharp.Cli.csproj" \
  --configuration "$configuration" \
  --no-build \
  --no-restore \
  --output "$package_directory" \
  -p:Version="$package_version" \
  -p:PackageVersion="$package_version"

package_path="$package_directory/Graphify.CSharp.${package_version}.nupkg"
test -f "$package_path"
dotnet nuget push "$package_path" --source "$feed_directory"
dotnet tool install \
  --tool-path "$tool_directory_net10" \
  --add-source "$feed_directory" \
  --ignore-failed-sources \
  --no-cache \
  --framework net10.0 \
  Graphify.CSharp \
  --version "$package_version"
dotnet tool install \
  --tool-path "$tool_directory_net11" \
  --add-source "$feed_directory" \
  --ignore-failed-sources \
  --no-cache \
  --framework net11.0 \
  Graphify.CSharp \
  --version "$package_version"

reference_fixture_output="$temporary_root/reference-fixture.json"
"$tool_directory_net10/graphify-csharp" \
  --input "$repository_root/tests/Fixtures/ReferenceFixture/ReferenceFixture.csproj" \
  --root "$repository_root" \
  --configuration "$configuration" \
  --target-framework net10.0 \
  --output "$reference_fixture_output"

for required_node_kind in parameter local type_parameter; do
  grep -q "\"node_kind\": \"$required_node_kind\"" "$reference_fixture_output" || {
    echo "Declaration fixture did not emit node kind '$required_node_kind'." >&2
    exit 1
  }
done
for required_declaration_kind in alias label range_variable local_constant record record_struct; do
  grep -q "\"declaration_kind\": \"$required_declaration_kind\"" "$reference_fixture_output" || {
    echo "Declaration fixture did not emit declaration kind '$required_declaration_kind'." >&2
    exit 1
  }
done
if grep -q 'System.ValueTuple' "$reference_fixture_output"; then
  echo "Declaration fixture emitted compiler-generated tuple implementation details." >&2
  exit 1
fi

csharp15_fixture="$repository_root/tests/Fixtures/CSharp15Fixture/CSharp15Fixture.csproj"
dotnet build "$csharp15_fixture" --configuration "$configuration"
csharp15_output="$temporary_root/csharp15.json"
csharp15_repeat_output="$temporary_root/csharp15-repeat.json"
for output_path in "$csharp15_output" "$csharp15_repeat_output"; do
  "$tool_directory_net11/graphify-csharp" \
    --input "$csharp15_fixture" \
    --root "$repository_root" \
    --configuration "$configuration" \
    --target-framework net11.0 \
    --output "$output_path"
done
cmp -s "$csharp15_output" "$csharp15_repeat_output"
grep -q '"declaration_kind": "union"' "$csharp15_output" || {
  echo "C# 15 fixture did not emit union declarations." >&2
  exit 1
}
grep -q '"is_closed": "true"' "$csharp15_output" || {
  echo "C# 15 fixture did not retain closed-hierarchy metadata." >&2
  exit 1
}
grep -q '"declaration_kind": "indexer"' "$csharp15_output" || {
  echo "C# 15 fixture did not emit extension indexers." >&2
  exit 1
}
grep -q 'CSharp15Fixture.BufferedValuesBuilder.Create' "$csharp15_output" || {
  echo "C# 15 fixture did not resolve its collection builder." >&2
  exit 1
}
grep -q 'CSharp15Fixture.LabeledJumpConsumer.Ordinary:Scan' "$csharp15_output" || {
  echo "C# 15 fixture did not emit labeled-jump consumer declarations." >&2
  exit 1
}
grep -q 'CSharp15Fixture.MemorySafetyConsumer.NativeValue' "$csharp15_output" || {
  echo "C# 15 fixture did not emit memory-safety type references." >&2
  exit 1
}

if [[ ! -d "$fixture_root/.git" ]]; then
  mkdir -p "$(dirname -- "$fixture_root")"
  git clone --filter=blob:none --no-checkout --depth 1 "$repository_url" "$fixture_root"
fi

configured_url="$(git -C "$fixture_root" config --get remote.origin.url)"
test "$configured_url" = "$repository_url" || {
  echo "Unexpected e2e repository URL: '$configured_url'" >&2
  exit 1
}

current_commit="$(git -C "$fixture_root" rev-parse HEAD 2>/dev/null || true)"
if [[ "$current_commit" != "$repository_commit" ]]; then
  git -C "$fixture_root" fetch --depth 1 origin "$repository_commit"
fi
if [[ ! -f "$fixture_root/Dapper/Dapper.csproj" ]]; then
  git -C "$fixture_root" checkout --detach "$repository_commit"
fi
test "$(git -C "$fixture_root" rev-parse HEAD)" = "$repository_commit"

dotnet restore "$fixture_root/Dapper/Dapper.csproj" \
  -p:TargetFrameworks="$target_framework" \
  -p:NuGetAudit=false
mkdir -p "$output_root"

case "$target_framework" in
  net11.*)
    real_world_tool="$tool_directory_net11"
    ;;
  *)
    real_world_tool="$tool_directory_net10"
    ;;
esac

for output_path in "$output_root/csharp.json" "$output_root/csharp-repeat.json"; do
  "$real_world_tool/graphify-csharp" \
    --input Dapper/Dapper.csproj \
    --root "$fixture_root" \
    --configuration "$configuration" \
    --target-framework "$target_framework" \
    --output "$output_path"
done

cmp -s "$output_root/csharp.json" "$output_root/csharp-repeat.json"

node_count="$(grep -c '"node_kind"' "$output_root/csharp.json")"
test "$node_count" -ge 500 || {
  echo "Real-world e2e produced too few nodes: $node_count" >&2
  exit 1
}

for required_node_kind in namespace type method constructor property field event parameter local type_parameter; do
  grep -q "\"node_kind\": \"$required_node_kind\"" "$output_root/csharp.json" || {
    echo "Real-world e2e did not emit node kind '$required_node_kind'." >&2
    exit 1
  }
done

grep -q '"declaration_kind": "enum_member"' "$output_root/csharp.json"
grep -q '"declaration_kind": "localfunction"' "$output_root/csharp.json"
grep -q '"relation": "inherits"' "$output_root/csharp.json"
grep -q '"relation": "implements"' "$output_root/csharp.json"

echo "real-world-e2e-ok package=$package_version commit=$repository_commit nodes=$node_count csharp15=ok output=$output_root/csharp.json"

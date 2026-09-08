#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
fixture_root="$repository_root/.e2e/dapper"
output_root="$fixture_root/graphify-out"
repository_url="${GRAPHIFY_CSHARP_E2E_REPOSITORY_URL:-https://github.com/DapperLib/Dapper.git}"
repository_commit="${GRAPHIFY_CSHARP_E2E_REPOSITORY_COMMIT:-6d48ef664acc7298c649e2d449d903b3360d5a90}"
target_framework="${GRAPHIFY_CSHARP_E2E_TARGET_FRAMEWORK:-net10.0}"

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
  git -C "$fixture_root" checkout --detach "$repository_commit"
fi
test "$(git -C "$fixture_root" rev-parse HEAD)" = "$repository_commit"

dotnet restore "$fixture_root/Dapper/Dapper.csproj" -p:TargetFrameworks="$target_framework"
mkdir -p "$output_root"

if [[ -n "${GRAPHIFY_CSHARP_TOOL:-}" ]]; then
  tool=("$GRAPHIFY_CSHARP_TOOL")
else
  tool=(dotnet run --project "$repository_root/src/Graphify.CSharp.Cli/Graphify.CSharp.Cli.csproj" --configuration Release --no-build --no-restore --)
fi

"${tool[@]}" \
  --input Dapper/Dapper.csproj \
  --root "$fixture_root" \
  --configuration Release \
  --target-framework "$target_framework" \
  --output "$output_root/csharp.json"

"${tool[@]}" \
  --input Dapper/Dapper.csproj \
  --root "$fixture_root" \
  --configuration Release \
  --target-framework "$target_framework" \
  --output "$output_root/csharp-repeat.json"

cmp -s "$output_root/csharp.json" "$output_root/csharp-repeat.json"

node_count="$(grep -c '"node_kind"' "$output_root/csharp.json")"
test "$node_count" -ge 500 || {
  echo "Real-world e2e produced too few nodes: $node_count" >&2
  exit 1
}

for required_node_kind in namespace type method constructor property field event; do
  grep -q "\"node_kind\": \"$required_node_kind\"" "$output_root/csharp.json" || {
    echo "Real-world e2e did not emit node kind '$required_node_kind'." >&2
    exit 1
  }
done

grep -q '"declaration_kind": "enum_member"' "$output_root/csharp.json"
grep -q '"declaration_kind": "localfunction"' "$output_root/csharp.json"
grep -q '"relation": "inherits"' "$output_root/csharp.json"
grep -q '"relation": "implements"' "$output_root/csharp.json"

echo "real-world-e2e-ok commit=$repository_commit nodes=$node_count output=$output_root/csharp.json"

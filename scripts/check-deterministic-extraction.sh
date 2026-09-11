#!/usr/bin/env bash
set -euo pipefail

if [[ $# -eq 0 ]]; then
  printf 'Usage: %s --input <solution-or-project> --root <repository-root> [CLI options]\n' "${0##*/}" >&2
  exit 64
fi

for argument in "$@"; do
  case "$argument" in
    --output|--output=*)
      printf '%s\n' '--output is managed by this script; pass the input/root/configuration/TFM options only.' >&2
      exit 64
      ;;
  esac
done

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cli_project="$repository_root/src/Graphify.CSharp.Cli/Graphify.CSharp.Cli.csproj"
tool_framework="${GRAPHIFY_CSHARP_TOOL_FRAMEWORK:-net10.0}"

temporary_directory="$(mktemp -d "${TMPDIR:-/tmp}/graphify-csharp-determinism.XXXXXX")"
trap 'rm -rf "$temporary_directory"' EXIT

dotnet build "$cli_project" --framework "$tool_framework" --configuration Release

dotnet run --project "$cli_project" --framework "$tool_framework" --configuration Release --no-build --no-restore -- \
  "$@" --output "$temporary_directory/first.json"
dotnet run --project "$cli_project" --framework "$tool_framework" --configuration Release --no-build --no-restore -- \
  "$@" --output "$temporary_directory/second.json"

if ! cmp -s "$temporary_directory/first.json" "$temporary_directory/second.json"; then
  printf '%s\n' 'Determinism check failed: repeated extraction outputs differ.' >&2
  exit 1
fi

if command -v sha256sum >/dev/null 2>&1; then
  digest="$(sha256sum "$temporary_directory/first.json" | awk '{print $1}')"
else
  digest="$(shasum -a 256 "$temporary_directory/first.json" | awk '{print $1}')"
fi

printf 'deterministic-extraction-ok %s\n' "$digest"

# Graphify C#

A headless Roslyn/MSBuild semantic enricher for
[Graphify](https://github.com/Graphify-Labs/graphify).

It emits deterministic C# declaration nodes, directed Roslyn-resolved semantic
relationships, provenance, and stable source locations in Graphify’s JSON shape.
Downstream Graphify queries can use the edges and namespace metadata to answer
repository-specific questions such as caller and zero-inbound-reference audits.

## Quick start

Run from a repository containing the solution or project you want to inspect:

```text
dotnet tool install --global Graphify.CSharp
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
```

## Integrate with Graphify

### Add the skills to a coding-agent project

Graphify’s general-purpose skill and this enricher’s C# skill are intended to
be installed together. Install Graphify’s skill for the agent platform you
use, then copy this repository’s
[`.agents/skills/graphify-csharp/SKILL.md`](.agents/skills/graphify-csharp/SKILL.md)
into the consuming repository at the same relative path:

```text
graphify install --platform codex
mkdir -p .agents/skills/graphify-csharp
cp /path/to/graphify-csharp/.agents/skills/graphify-csharp/SKILL.md \
  .agents/skills/graphify-csharp/SKILL.md
```

Keep the two skills separate. The `graphify` skill handles generic extraction,
queries, paths, and exports; `graphify-csharp` adds the C# workflow and tells
the agent to run `graphify-csharp` before Graphify consumes the graph. If your
agent uses a different project-skill directory, place the same C# `SKILL.md`
there according to that agent’s conventions.

Run the enricher from the root of the repository being analyzed, before every
Graphify rebuild. The output is already Graphify extraction JSON, so Graphify
can build its directed graph from the file:

```text
dotnet tool install --global Graphify.CSharp

graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json

graphify query "Which methods call the service?" \
  --graph ./graphify-out/csharp.json
```

For a normal C# repository, make those commands the repository’s Graphify
entry point so the semantic enricher runs every time. For example, save this
as `scripts/graphify-csharp.sh` and use it instead of calling `graphify`
directly:

```bash
#!/usr/bin/env bash
set -euo pipefail

root="$(git rev-parse --show-toplevel)"
input="${GRAPHIFY_CSHARP_INPUT:-$root/src/MyProduct.sln}"
output="$root/graphify-out/csharp.json"

graphify-csharp \
  --input "$input" \
  --root "$root" \
  --configuration "${GRAPHIFY_CSHARP_CONFIGURATION:-Release}" \
  --output "$output"

exec graphify query "$@" --graph "$output"
```

Make it executable with `chmod +x scripts/graphify-csharp.sh`, then run
`scripts/graphify-csharp.sh "Which methods call the service?"`. The wrapper
regenerates the semantic layer before every query. If your Graphify workflow
uses `extract`, `path`, `explain`, or an export command instead, keep the same
first `graphify-csharp` step and pass `--graph ./graphify-out/csharp.json` to
that command.

The raw `csharp.json` file is the authoritative C# evidence and preserves
parallel relationships such as `calls` and `overrides`. Graphify’s clustered
NetworkX view may normalize multiple relationships between the same endpoints
into one edge, so retain the raw file when an audit depends on relation-level
detail. If the repository also contains non-C# material, keep its normal
Graphify extraction as a separate graph and merge the two Graphify JSON
documents with Graphify’s `merge-graphs` command. Do not merge this output
with a name-only C# extraction without an explicit ID-join policy.

The package is currently built from this repository as version `0.1.0` while
the API and Graphify integration settle. For local development, replace the
install command with:

```text
dotnet run --project src/Graphify.CSharp.Cli -- --input ./src/MyProduct.sln --root .
```

`--target-framework` is optional. The loader resolves a single project target
automatically; pass it when a project targets multiple frameworks. An ambiguous
multi-target project fails with an actionable message instead of producing a
mixed graph.

## Output

The output keeps Graphify’s required `nodes`, `edges`, and `hyperedges` arrays.
Edges are directed from source/caller to target/contract and use `EXTRACTED` for
Roslyn-resolved facts. v0.1 emits `calls`, `references`, `inherits`,
`implements`, and `overrides`; node properties include the full symbol key,
namespace, project, target framework, and declaration kind. The catalog covers
namespaces, named types, constructors, methods/operators/local functions,
properties/indexers, fields/enum values, and events. `graphify_csharp` contains
only the versioned extractor metadata and loader diagnostics.

The enricher does not classify callers or decide whether a declaration is safe
to remove. Reflection, dependency injection, generated code, native callbacks,
and other runtime mechanisms are outside static extraction and must be handled
by the consuming analysis.

## Scope of v0.1

Included:

- `.sln`, `.slnx`, and `.csproj` loading through MSBuildWorkspace;
- overload-aware symbol identity including project and TFM context;
- direct calls, constructors, method groups, properties, fields, enum values,
  events, declaration-header, attribute, generic, and `typeof` references;
- inheritance, interface implementation, and virtual override relationships;
- cross-project symbol resolution with conservative ambiguity handling;
- stable Graphify JSON and a dependency-free command-line parser.

Not a runtime reachability proof. Interface/virtual dispatch expansion,
reflection heuristics, DI container modeling, and host callbacks are deliberately
bounded in v0.1 and will be added only with explicit provenance and fixtures.
Compiler-generated members, parameters, and local variables are not separate
graph nodes in v0.1.

## Development

```text
dotnet test Graphify.CSharp.sln --configuration Release
dotnet build Graphify.CSharp.sln --configuration Release
dotnet pack src/Graphify.CSharp.Cli --configuration Release
```

To verify byte-for-byte repeatability against a fixture or another solution:

```text
./scripts/check-deterministic-extraction.sh \
  --input ./src/MyProduct/MyProduct.sln \
  --root . \
  --configuration Release
```

The pinned real-world semantic end-to-end gate restores, builds, and tests this
solution, packs the CLI, installs that package into an isolated temporary tool
directory, then clones a third-party C# project into the ignored `.e2e/`
directory, checks multiple declaration kinds and relationships, and runs
extraction twice:

```text
./scripts/run-real-world-e2e.sh
```

The temporary feed and tool directory are removed on exit; the pinned source
checkout and Graphify output remain under `.e2e/` for inspection. Override the
fixture URL, commit, TFM, configuration, or local package version with the
`GRAPHIFY_CSHARP_E2E_*` environment variables when testing another pinned
fixture.

The implementation slices and acceptance gates are in [PLAN.md](PLAN.md). The
repository’s reusable development contract is in
[.agents/skills/graphify-csharp/SKILL.md](.agents/skills/graphify-csharp/SKILL.md).
See [docs/USAGE.md](docs/USAGE.md) for output details and
[docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) for the supported v0.1 path, and
[docs/RELEASING.md](docs/RELEASING.md) for NuGet publishing setup.

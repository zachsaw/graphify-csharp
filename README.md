# Graphify C#

[![CI](https://github.com/zachsaw/graphify-csharp/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/zachsaw/graphify-csharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Graphify.CSharp.svg)](https://www.nuget.org/packages/Graphify.CSharp)
[![NuGet downloads](https://img.shields.io/nuget/dt/Graphify.CSharp.svg)](https://www.nuget.org/packages/Graphify.CSharp)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A headless Roslyn/MSBuild semantic enricher for
[Graphify](https://github.com/Graphify-Labs/graphify).

It emits deterministic C# declaration nodes, directed Roslyn-resolved semantic
relationships, provenance, and stable source locations in Graphify’s JSON shape.
Downstream Graphify queries can use the edges and namespace metadata to answer
repository-specific questions such as caller and zero-inbound-reference audits.

## Quick start

Run from a repository containing the solution, project, or file-based app you
want to inspect:

```text
dotnet tool install --global Graphify.CSharp --framework net10.0
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
```

The package contains one tool asset for each supported .NET runtime. Use the
`net10.0` asset for the normal C# 14 path. For a project using C# 15 syntax,
install or update the same package with `--framework net11.0`; that asset uses
the .NET 11 Roslyn compiler surface:

```text
dotnet tool update --global Graphify.CSharp --framework net11.0
```

The install/update `--framework` selects the tool runtime. The command’s
`--target-framework` option selects the analyzed project compilation and is
still only needed when that project is multi-targeted or when a particular
target must be inspected.

An SDK file-based app can be passed directly when there is no `.csproj` yet:

```text
graphify-csharp \
  --input ./src/App.cs \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
```

The tool asks the installed SDK to convert the file-based app temporarily,
honors its `#:sdk`, `#:property`, `#:package`, `#:project`, and `#:include`
directives, and remaps source locations back to the repository. The SDK and
any referenced packages/projects must be available on the host.

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
dotnet tool install --global Graphify.CSharp --framework net10.0

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

The CLI currently writes one complete Graphify extraction document. Large
repositories can be sharded later, but every shard must remain a complete
`nodes`/`edges`/`hyperedges` document with stable IDs. Plain JSON Lines
fragments are not Graphify extraction files by themselves.

Graphify’s current `merge-graphs` command is intended for independent
repositories or graph sources: it prefixes each input’s IDs and normalizes the
merged view. It should not be used as the same-repository shard merger when
stable C# IDs or parallel relations matter. A future shard mode needs a
deterministic same-repository merger that unions nodes by ID, preserves the
multigraph edge identity, validates cross-shard endpoints, and emits the same
complete Graphify document.

The package is currently built from this repository as version `0.1.0` while
the API and Graphify integration settle. For local development, replace the
install command with:

```text
dotnet run --project src/Graphify.CSharp.Cli --framework net10.0 -- --input ./src/MyProduct.sln --root .
```

`--target-framework` is optional. The loader resolves a single project target
automatically; pass it when a project targets multiple frameworks. An ambiguous
multi-target project fails with an actionable message instead of producing a
mixed graph. For a file-based app it is forwarded to the SDK conversion/restore
step when supplied.

## Output

The output keeps Graphify’s required `nodes`, `edges`, and `hyperedges` arrays.
Edges are directed from source/caller to target/contract and use `EXTRACTED` for
Roslyn-resolved facts. v0.1 emits `calls`, `references`, `inherits`,
`implements`, and `overrides`; compiler-bound call arguments also reference
their source formal-parameter declarations. Node properties include the full
symbol key, namespace, project, target framework, and declaration kind. The
catalog covers
namespaces, named types, constructors, methods/operators/local functions,
properties/indexers, fields/enum values, events, parameters, locals, type
parameters, aliases, labels, and query range variables. `graphify_csharp`
contains only the versioned extractor metadata and loader or
declaration-extraction diagnostics. If Roslyn exposes a source declaration shape
that cannot yet be given a stable identity, the enricher skips that declaration,
records an actionable diagnostic with its source location, and continues emitting
the rest of the graph. C# 15 closed hierarchy types additionally carry
`is_closed=true`.

The enricher does not classify callers or decide whether a declaration is safe
to remove. Reflection, dependency injection, generated code, native callbacks,
and other runtime mechanisms are outside static extraction and must be handled
by the consuming analysis.

## Scope of v0.1

Included:

- `.sln`, `.slnx`, and `.csproj` loading through MSBuildWorkspace, plus SDK
  file-based `.cs` apps with source-location remapping;
- overload-aware symbol identity including project and TFM context;
- direct calls, constructors, method groups, properties, fields, enum values,
  events, scoped declarations, formal call parameters, declaration-header,
  attribute, generic, and `typeof` references;
- compiler-selected members for operators, conversions, deconstruction,
  foreach/await/using, property/event accessors, patterns, ranges, collection
  expressions, interpolated string handlers, and fixed/pointer syntax;
- inheritance, interface implementation, and virtual override relationships;
- C# 14 extension blocks, field-backed properties, partial constructors/events,
  explicit compound-assignment operators, and newer lambda/assignment forms;
- C# 15 collection-expression arguments, union declarations and case-type
  references, closed hierarchies, extension indexers, labeled jumps, and
  memory-safety syntax when the `net11.0` tool asset is selected;
- cross-project symbol resolution with conservative ambiguity handling;
- stable Graphify JSON and a dependency-free command-line parser.

Not a runtime reachability proof. Interface/virtual dispatch expansion,
reflection heuristics, DI container modeling, and host callbacks are deliberately
bounded in v0.1 and will be added only with explicit provenance and fixtures.
Unnamed syntax artifacts and compiler-generated implementation details are not
separate graph nodes in v0.1.

## Development

```text
dotnet test Graphify.CSharp.sln --configuration Release
dotnet build Graphify.CSharp.sln --configuration Release
dotnet pack src/Graphify.CSharp.Cli --configuration Release
```

The full solution and package commands validate both target assets and require
the .NET 10 and .NET 11 SDKs. A .NET 10-only checkout can still run the
focused `net10.0` build/test commands with `--framework net10.0`.

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

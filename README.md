# Graphify C#

[![CI](https://github.com/zachsaw/graphify-csharp/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/zachsaw/graphify-csharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Graphify.CSharp.svg)](https://www.nuget.org/packages/Graphify.CSharp)
[![NuGet downloads](https://img.shields.io/nuget/dt/Graphify.CSharp.svg)](https://www.nuget.org/packages/Graphify.CSharp)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## Give your LLM agent an IDE’s semantic map

We humans have the luxury of Rider.

When we need to understand a C# codebase, we jump to an implementation, walk
up to a base class, follow derived types, find usages, inspect overrides, trace
a call hierarchy, and let the compiler distinguish overloaded and generic
symbols for us. The IDE quietly answers the hard questions while we navigate.

Most coding agents start somewhere very different: a terminal, text search,
file snippets, and a broad repository graph. That is useful for finding words
and broad relationships. It is not the same as understanding the program. Two
methods can have the same name and completely different contracts. An
interface call can land on an override in another project. A generic invocation
can bind to one precise method while other textually similar candidates remain
irrelevant.

[Graphify](https://github.com/Graphify-Labs/graphify) gives agents a useful
repository graph, but Graphify alone is not a C# compiler. A general graph can
show that things are related; it cannot replace Roslyn’s symbol binding when an
agent needs the exact declaration, caller, implementation, override, or
parameter involved.

Graphify C# adds that missing semantic layer.

It is a headless Roslyn/MSBuild index that gives an agent the compiler’s answer
as a deterministic JSON document. Use it directly from an agent, script, or
`jq`; add Graphify when you want graph traversal, clustering, explanations, and
exports.

> The semantic navigation layer your C# agent should have had from day one.

## From IDE navigation to agent evidence

| What a human does in Rider | What the agent gets from Graphify C# |
| --- | --- |
| Find usages | Directed, compiler-resolved `calls` and `references` edges |
| Jump to implementation | `implements` edges to the exact interface or contract |
| Move between base and derived types | `inherits` and `overrides` edges |
| Disambiguate overloads and generics | Stable symbol identities with bound parameter and type information |
| Inspect a large solution | Project, target-framework, source-location, and provenance metadata |
| Keep navigating while editing | A warm watcher that prepares changes and publishes a complete JSON snapshot on demand |

The result is not a text dump with better formatting. It is the semantic
evidence an agent can use to navigate a C# program without an IDE.

## Why this exists

The question that exposed the gap was simple:

> “Which methods are actually used, and which are only reachable from tests?”

Text search and broad graph relationships are not enough. Generic types,
overloads, inheritance, extension members, generated compiler bindings, and
cross-project references make name-based answers unreliable. Before an agent can
make a useful usage or dead-code assessment, it needs the same symbol
relationships a human gets from IDE navigation.

Graphify C# loads the project through MSBuild and Roslyn and emits the semantic
facts an agent needs:

- exact symbol identity instead of names that collide;
- callers and referencers with edge direction preserved;
- implementations, inheritance, and overrides;
- every source declaration that can be represented;
- source locations and project/target-framework provenance;
- deterministic output that can be checked into an audit workflow or regenerated
  on demand.

The extractor reports evidence. Your agent decides what that evidence means:
test-only usage, zero observed references, a safe deletion candidate, or
something that needs human review.

## What can an agent ask now?

- What calls this exact overload or constructor?
- Which source declarations reference this field, property, event, type, or enum member?
- Which classes inherit from this type or implement this interface?
- Which overrides satisfy this virtual or interface member?
- Which arguments bind to which formal parameters?
- Which declarations have zero observed inbound static references?
- Which references originate from namespaces or projects named Tests?

That is the difference between asking an agent to search a repository and giving
it a semantic map of the repository.

## Feature spotlight: usage and dead-code audits

Every emitted declaration has a stable identity. Every extracted relationship
points from the declaration where it was observed to the declaration it
resolved to.

To find callers, inspect incoming `calls` edges. To find other referencers,
inspect incoming `references` edges. To find test-only usage, classify the
caller’s namespace or project metadata. To find zero-reference declarations,
compare the declaration catalog with inbound edges.

No special test framework integration is required. No commercial analyzer is
required. The output is plain Graphify-compatible JSON.

Static analysis is still static analysis: reflection, dependency injection,
generated code, native callbacks, and dynamic dispatch can create runtime
reachability that is not visible as a direct Roslyn edge. The tool makes that
boundary explicit instead of pretending the answer is certain.

## Quick start

Install the global tool and index a solution:

~~~text
dotnet tool install --global Graphify.CSharp --framework net10.0

graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
~~~

The result is one complete JSON document with `nodes`, `edges`, and
`hyperedges`. It works whether or not Graphify is installed.

For C# 15 syntax, select the .NET 11 asset from the same multi-targeted package:

~~~text
dotnet tool update --global Graphify.CSharp --framework net11.0
~~~

The two framework options answer different questions:

- install `--framework` selects the runtime and Roslyn asset used by the tool;
- `--target-framework` selects the analyzed compilation when the input
  project targets multiple frameworks.

For a single-target project, `--target-framework` is optional.

Supported inputs are `.sln`, `.slnx`, `.csproj`, and SDK file-based
`.cs` apps. MSBuild, the selected SDK, referenced projects, and packages must
be available on the host.

## Use it without Graphify

The output is self-contained JSON. An agent can read it directly, and existing
command-line tools can inspect it without another service or database.

List all methods:

~~~text
jq '.nodes[] | select(.properties.declaration_kind == "method")' \
  graphify-out/csharp.json
~~~

Find a declaration by semantic key, then inspect its incoming callers and
referencers:

~~~text
node_id="$(jq -r '
  .nodes[]
  | select(.properties.symbol_key | contains("MyProduct.Services.OrderService"))
  | .id
  ' graphify-out/csharp.json | head -1)"

jq --arg target "$node_id" \
  '[.edges[] | select(.target == $target)]' \
  graphify-out/csharp.json
~~~

The same document can be consumed by a Python, C#, or agent-side analysis
script. There is no Graphify runtime dependency in the extractor.

## Use it with Graphify

Graphify C# is an enricher, not a second graph database. It produces the
compiler-backed C# layer; Graphify provides the general graph workflows on top.

~~~text
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json

graphify query "Which methods call the order service?" \
  --graph ./graphify-out/csharp.json
~~~

If you want Graphify’s query, path, explanation, and export workflows in a
coding-agent project, install Graphify’s general skill and add this repository’s
C# skill alongside it:

~~~text
graphify install --platform codex
mkdir -p .agents/skills/graphify-csharp
cp /path/to/graphify-csharp/.agents/skills/graphify-csharp/SKILL.md \
  .agents/skills/graphify-csharp/SKILL.md
~~~

Graphify is optional. If you only want compiler-backed C# facts, copy the C#
skill and use `graphify-csharp` directly; do not install Graphify or its general
skill.

### Install the skills globally

If you use coding agents across multiple repositories, install this skill in
your user skill directory instead of copying it into every project. Graphify’s
general skill is optional: install it only if you also want Graphify’s graph
workflows.

For Codex:

~~~bash
mkdir -p ~/.codex/skills/graphify-csharp
cp /path/to/graphify-csharp/.agents/skills/graphify-csharp/SKILL.md \
  ~/.codex/skills/graphify-csharp/SKILL.md
~~~

If Graphify is also installed and you want its general agent workflow, add its
skill separately:

~~~text
graphify install --platform codex
~~~

For Claude Code:

~~~bash
mkdir -p ~/.claude/skills/graphify-csharp
cp /path/to/graphify-csharp/.agents/skills/graphify-csharp/SKILL.md \
  ~/.claude/skills/graphify-csharp/SKILL.md
~~~

Claude Code loads personal skills from `~/.claude/skills` across all projects;
the directory name also makes this available as `/graphify-csharp`. See the
[Claude Code skills documentation](https://code.claude.com/docs/en/skills) for
the current global and project-local locations. Restart an already-running
agent session after installing or updating a skill.

Global skills are personal to the machine. For a team-shared setup, commit the
skill under `.agents/skills/graphify-csharp` for Codex-compatible agents and/or
`.claude/skills/graphify-csharp` for Claude Code, then keep the committed copy
in sync with this repository’s skill.

Keep the responsibilities clear:

- Graphify handles general extraction, queries, paths, explanations, clustering,
  and exports.
- `graphify-csharp` refreshes compiler-resolved C# evidence before Graphify
  consumes it.

### Run the enricher every time Graphify runs

For a normal C# repository, make a wrapper the repository’s Graphify entry point:

~~~bash
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

exec graphify "$@" --graph "$output"
~~~

Save it as `scripts/graphify-csharp.sh`, make it executable, and call it with
the Graphify subcommand and arguments:

~~~text
chmod +x scripts/graphify-csharp.sh
scripts/graphify-csharp.sh query "Which methods call the order service?"
~~~

Use the same first `graphify-csharp` step before `path`, `explain`, or an
export command. Keep `graphify-out/csharp.json` as the authoritative C# evidence
when an audit depends on parallel relationships or exact edge provenance.

## What gets indexed

### Declarations

The catalog covers source declarations for:

- namespaces, classes, structs, interfaces, records, record structs, enums,
  delegates, and C# 15 union declarations;
- constructors, methods, operators, local functions, properties, indexers,
  fields, enum members, and events;
- parameters, locals, type parameters, aliases, labels, and query range
  variables.

### Compiler-resolved relationships

Roslyn resolves relationships instead of comparing names:

- direct calls, constructor calls, method groups, property and event access;
- field, enum-value, type, attribute, generic, `typeof`, and declaration-header
  references;
- `inherits`, `implements`, and `overrides` relationships;
- compiler-selected operators, conversions, deconstruction, `foreach`,
  `await`, `using`, patterns, ranges, collection expressions, interpolated
  string handlers, and fixed/pointer syntax;
- references from invocation and constructor arguments to their bound source
  formal parameters;
- cross-project references with overload-aware, project/TFM-aware symbol identity.

C# 14 coverage includes extension blocks and receiver parameters, field-backed
properties, partial constructors and events, explicit compound-assignment
operators, and newer lambda and assignment forms. C# 15 coverage is available
through the `net11.0` asset for collection-expression arguments, union/case
relationships, closed hierarchies, extension indexers, labeled branches, and
memory-safety syntax.

### Evidence and diagnostics

Every semantic edge is directed from its source declaration to its target and
marked as extracted compiler evidence. Stable IDs include project and target
framework, so overloads and multi-project symbols do not collapse into one name.

If Roslyn exposes a declaration or operation that this version cannot represent,
the tool records a diagnostic identifying the affected item or document, with a
source location where available, and continues extracting unaffected
declarations and documents. Any omitted evidence is therefore visible instead
of causing the run to crash.

## Warm indexing for agent workflows

For repeated work, start one watcher for the exact input, repository root,
configuration, target framework, and output path:

~~~text
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json \
  --watch
~~~

The watcher subscribes before cold startup, keeps the Roslyn workspace warm,
queues file-system hints away from the OS callback, and can prepare dirty
projects in the background.

A normal invocation is the explicit refresh barrier. It connects to a matching
watcher, waits until indexing is ready, and ensures that a complete JSON
document is current, publishing it atomically when needed. Ordinary file
changes do not silently rewrite the public output:

~~~text
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
~~~

If no matching watcher exists, the same command performs a cold one-shot
refresh. A watcher with a different output path is not reused or redirected.
Use `--rebuild` to invalidate the incremental cache and rebuild from scratch.

The watcher also runs a backup inventory scan. Watcher errors, native-buffer
overflow, bounded-queue overflow, missing roots, and failed inventory scans
invalidate the warm session; recovery recreates subscriptions and completes a
cold reconciliation before serving the next refresh. The previous complete JSON
remains readable while recovery runs, and recovery does not delete user files.

## Determinism and real-world validation

The same input, repository root, configuration, target framework, and toolchain
produce byte-for-byte repeatable output:

~~~text
./scripts/check-deterministic-extraction.sh \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release
~~~

The repository contains two repeatable E2E paths. The real-world script builds
and installs a local package, clones a pinned third-party C# fixture into the
ignored `.e2e` directory, exercises complex declarations and relationships, and
runs both supported runtime assets. The watcher script packages and installs
the tool in an isolated temporary directory and validates the watcher lifecycle
for one selected asset:

~~~text
./scripts/run-real-world-e2e.sh
./scripts/run-watcher-e2e.sh
~~~

## Boundaries

This is static compiler evidence, not a runtime reachability proof. Treat zero
inbound references as “zero observed static references,” not as automatic
permission to delete code.

The tool currently writes one complete JSON document. It does not emit shards,
require a database, or require Rider, InspectCode, or Graphify to run. The
`net11.0` asset and C# 15 syntax support require the corresponding .NET 11
SDK/runtime toolchain.

## Development

~~~text
dotnet restore Graphify.CSharp.sln
dotnet build Graphify.CSharp.sln --configuration Release
dotnet test Graphify.CSharp.sln --configuration Release
dotnet pack src/Graphify.CSharp.Cli --configuration Release
~~~

See [docs/USAGE.md](docs/USAGE.md) for detailed commands,
[docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) for the supported compiler and
language surface, [docs/INCREMENTAL_INDEXING.md](docs/INCREMENTAL_INDEXING.md)
for the refresh lifecycle, and [docs/RELEASING.md](docs/RELEASING.md) for NuGet
publishing.

## License

MIT. See [LICENSE](LICENSE).

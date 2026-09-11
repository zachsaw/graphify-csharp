# graphify-csharp 🚀

> **Give coding agents compiler-accurate Find Usages for C#.**

[![NuGet Version](https://img.shields.io/nuget/v/Graphify.CSharp.svg)](https://www.nuget.org/packages/Graphify.CSharp)
[![CI](https://github.com/zachsaw/graphify-csharp/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/zachsaw/graphify-csharp/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

`graphify-csharp` is a free, headless Roslyn/MSBuild indexer that turns C#
source into deterministic, queryable semantic evidence: compiler-resolved
callers, references, implementations, inheritance, and overrides—even across
overloads, generics, and projects.

Think of it as the semantic-navigation slice of Rider/ReSharper, exported for
Codex, Claude Code, and other coding agents.

**MIT licensed · No IDE · No compiled project DLL required · No database · Graphify optional**

## Stop making your agent guess

Suppose you ask:

> **Which methods are used only by tests?**

A text search can find matching spellings. It cannot reliably tell which
overload was bound, which project the caller belongs to, or whether an
interface implementation is the symbol you meant.

Graphify C# loads the project through MSBuild and asks Roslyn what every symbol
actually means. It emits stable identities and directed relationships that an
agent can inspect instead of infer:

| Without semantic indexing | With Graphify C# |
| --- | --- |
| Matching names look like usages | Roslyn resolves the exact declaration |
| Overloads and generics are ambiguous | Bound signatures and project/TFM identity are retained |
| Test-only usage requires manual inspection | Every caller carries project, namespace, and source location |
| Type relationships are reconstructed from text | `inherits`, `implements`, and `overrides` are explicit edges |

For example, this repository contains an internal
`DeclarationCatalogBuilder.ForTesting(...)` method. From the extracted graph,
an agent can see one compiler-resolved incoming call:

```text
Graphify.CSharp.Roslyn.DeclarationCatalogBuilder.ForTesting(...)
└── called by Graphify.CSharp.Tests.Roslyn.CSharp14FeatureTests
    at tests/Graphify.CSharp.Tests/Roslyn/CSharp14FeatureTests.cs:143
```

That is semantic evidence, not a text-match count. A consumer can classify the
caller by project or namespace convention and report the method as test-only
for human review.

## Quick start

### 1. Install

```bash
dotnet tool install --global Graphify.CSharp --framework net10.0
```

### 2. Index your codebase

```bash
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
```

The result is one complete JSON document containing `nodes`, `edges`, and
`hyperedges`. It can be read directly by an agent, queried with `jq`, consumed
from your own code, or passed to Graphify.

Supported inputs are `.sln`, `.slnx`, `.csproj`, and SDK file-based `.cs` apps.
The repository's SDKs, packages, and MSBuild inputs must be available locally.

### 3. Teach your agent to use it

The included [`graphify-csharp` skill](.agents/skills/graphify-csharp/SKILL.md)
teaches an agent when to refresh the index, how to follow semantic edges, and
where static analysis stops.

Install it in a Codex-compatible project:

```bash
mkdir -p .agents/skills/graphify-csharp
curl -fsSL \
  https://raw.githubusercontent.com/zachsaw/graphify-csharp/main/.agents/skills/graphify-csharp/SKILL.md \
  -o .agents/skills/graphify-csharp/SKILL.md
```

For Claude Code, use `.claude/skills/graphify-csharp/SKILL.md` instead. Reload
an agent session after installing or updating the skill.

If you do not use skills, add this to your project instructions:

> For C# structure and usage questions, refresh
> `graphify-out/csharp.json` with `graphify-csharp` before answering. Identify
> declarations by `symbol_key` and inspect incoming `calls` and `references`
> edges. Treat zero inbound edges as observed static evidence, not proof of
> runtime unreachability.

Now ask your agent:

- What calls this exact overload or constructor?
- Which source declarations reference this field, property, event, or type?
- Which classes implement this interface?
- Which members override this virtual or interface member?
- Which declarations have zero observed inbound references?
- Which methods are referenced only from test projects?

## From IDE navigation to agent evidence

| What a developer does in Rider | What an agent gets from Graphify C# |
| --- | --- |
| Find Usages | Directed, compiler-resolved `calls` and `references` edges |
| Jump to Implementation | `implements` edges to the exact interface contract |
| Navigate base and derived types | `inherits` and `overrides` edges |
| Disambiguate overloads and generics | Stable symbol identities with bound signature information |
| Inspect a large solution | Project, target-framework, source-location, and provenance metadata |
| Keep navigating while editing | Incremental indexing with an optional warm watcher |

The extractor supplies the facts. Your agent or downstream consumer decides
what those facts mean: test-only usage, zero observed references, a deletion
candidate, or something requiring human review.

## Where it fits

Graphify C# deliberately covers a focused layer:

- **Rider and ReSharper** provide interactive navigation, inspections,
  refactorings, and quick fixes for developers inside an IDE.
- **NDepend** provides a broad, commercial architecture and code-quality suite
  built around dependency analysis, metrics, rules, reports, baselines, and
  visualizations.
- **Graphify C#** provides source-level C# semantic evidence for coding agents,
  headlessly and in an open format.

There is real overlap with NDepend around callers, dependencies, inheritance,
and dead-code investigation. The difference is the product boundary: Graphify
C# is not a free NDepend clone or an IDE replacement. It is a Roslyn-native
semantic index that other tools and agents can build on.

## Use it with Graphify—or without it

Graphify C# is standalone. It does not invoke, load, or require Graphify.

Without Graphify, query the JSON with an agent, `jq`, C#, Python, or any other
consumer. For example, list every indexed method:

```bash
jq '.nodes[] | select(.properties.node_kind == "method")' \
  graphify-out/csharp.json
```

With Graphify, refresh the C# evidence and use its higher-level query, path,
explanation, clustering, and export workflows:

```bash
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json

graphify query "Which methods call the order service?" \
  --graph ./graphify-out/csharp.json
```

Graphify remains the general graph workflow. `graphify-csharp` contributes the
C# layer where compiler binding matters.

## What gets indexed

### Source declarations

- Namespaces, classes, structs, interfaces, records, enums, and delegates
- Constructors, methods, operators, and local functions
- Properties, indexers, fields, enum members, and events
- Parameters, locals, type parameters, aliases, labels, and query range variables

### Compiler-resolved relationships

- Direct calls, constructor calls, method groups, and member access
- Field, type, attribute, generic, `typeof`, and declaration-header references
- `inherits`, `implements`, and `overrides`
- Compiler-selected operators, conversions, deconstruction, `foreach`,
  `await`, `using`, patterns, ranges, and collection expressions
- Invocation and constructor arguments bound to source formal parameters
- Cross-project relationships with overload-aware, project/TFM-aware identity

Every edge points from the declaration where the relationship was observed to
the declaration Roslyn resolved. Source location and provenance are retained.
Unsupported semantic shapes are reported as diagnostics instead of silently
disappearing or crashing the entire extraction.

See [Compatibility](docs/COMPATIBILITY.md) for the complete language and
compiler-feature matrix.

## Keep the index warm

For repeated agent work, start a watcher:

```bash
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json \
  --watch
```

The watcher keeps the Roslyn workspace warm and prepares changed projects in
the background. A normal `graphify-csharp` invocation acts as an explicit
refresh barrier and returns only after a complete JSON snapshot is current.

If no matching watcher is running, the same command performs a one-shot
refresh. Use `--rebuild` to invalidate the incremental cache.

See [Usage](docs/USAGE.md) and
[Incremental indexing](docs/INCREMENTAL_INDEXING.md) for watcher ownership,
filtering, recovery, and cache behavior.

## Runtime and language support

The package contains two tool assets:

| Tool asset | Runtime | Compiler surface |
| --- | --- | --- |
| `net10.0` | .NET 10 | Roslyn 5.9 / C# 14 |
| `net11.0` | .NET 11 | .NET 11 SDK Roslyn / C# 15 preview |

Install or update the package with `dotnet tool ... --framework` to select the
tool runtime and Roslyn asset:

```bash
dotnet tool update --global Graphify.CSharp --framework net11.0
```

This is separate from the optional `--target-framework` argument, which chooses
one analyzed compilation when an input project targets multiple frameworks.
Single-target projects do not need `--target-framework`.

## Static-analysis boundary

Graphify C# reports what Roslyn can observe statically. Reflection, dependency
injection, native callbacks, dynamic invocation, and code absent from the
loaded compilation may create runtime relationships that are not represented
as direct edges.

Consequently:

- zero inbound references means **zero observed static references**;
- a test-only result depends on your project or namespace classification; and
- every deletion candidate still requires judgment.

The tool exposes this boundary instead of pretending static evidence is a
runtime reachability proof.

## Development

```bash
dotnet restore Graphify.CSharp.sln
dotnet build Graphify.CSharp.sln --configuration Release
dotnet test Graphify.CSharp.sln --configuration Release
dotnet pack src/Graphify.CSharp.Cli --configuration Release
```

More detail:

- [Usage](docs/USAGE.md)
- [Compatibility](docs/COMPATIBILITY.md)
- [Incremental indexing design](docs/INCREMENTAL_INDEXING.md)
- [Release and NuGet publishing](docs/RELEASING.md)

## License

MIT. See [LICENSE](LICENSE).

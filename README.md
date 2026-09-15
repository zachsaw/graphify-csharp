# graphify-csharp 🚀

> **Give coding agents compiler-accurate Find Usages for C#.**

[![NuGet Version](https://img.shields.io/nuget/v/Graphify.CSharp.svg)](https://www.nuget.org/packages/Graphify.CSharp)
[![CI](https://github.com/zachsaw/graphify-csharp/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/zachsaw/graphify-csharp/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

`graphify-csharp` is a free, headless Roslyn/MSBuild indexer for coding agents.
It turns a C# solution or project into deterministic semantic evidence: exact
callers, references, implementations, inheritance, overrides, and argument
bindings—even across overloads, generics, and project boundaries.

Think of the semantic-navigation part of Rider or ReSharper, exported for
Codex, Claude Code, Cursor, and other tools that need to reason about a C#
codebase without guessing from filenames or text matches.

MIT licensed · No IDE · No prebuilt project DLL as input · No database ·
Graphify optional

## The problem

Ask an agent:

> Which methods are used only by tests?

Text search can find a spelling. It cannot reliably tell whether a call binds
to the `int` or `string` overload, whether an interface member dispatches to a
derived implementation, or which project a caller belongs to. That is how
plausible-looking dead-code reports become unsafe.

Graphify C# loads the evaluated project with MSBuild and asks Roslyn which
symbols and relationships the compiler resolved:

| Text search | Graphify C# |
| --- | --- |
| Matching names look like usages | Exact declarations and bound symbols |
| Overloads and generics are ambiguous | Stable identities retain signatures and project/TFM provenance |
| Test-only usage needs manual sorting | Callers include project, namespace, and source location |
| Inheritance is reconstructed from names | `inherits`, `implements`, and `overrides` are explicit relationships |

The tool reports the evidence. Your agent can then classify callers using the
repository's naming convention, such as projects or namespaces containing
`Tests`, and decide what deserves human review.

## Quick start

### 1. Install the global tool

```bash
dotnet tool install --global Graphify.CSharp --framework net10.0
```

Use the `net11.0` asset for C# 15 preview input when the .NET 11 SDK is
installed:

```bash
dotnet tool install --global Graphify.CSharp --framework net11.0
```

If the tool is already installed, use `dotnet tool update` with the same
`--framework` instead.

### 2. Export a complete graph

From a repository containing one solution or project, the shortest form is:

```bash
graphify-csharp
```

It discovers one `.sln`/`.slnx` or, when no solution exists, one `.csproj`
directly in the current directory and writes:

```text
./graphify-out/csharp.json
```

For a repository whose solution is under `src`, be explicit:

```bash
graphify-csharp export \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
```

The output is one complete JSON document with `nodes`, `edges`, and
`hyperedges`. It can be inspected by an agent, queried with `jq`, consumed by
your own program, or passed to the optional Graphify executable.

Upgrading from v0.1? See the concise [v0.2 migration guide](docs/MIGRATING_TO_V0.2.md)
for the `watch`/`export --instance` command split, output changes, and path
semantics.

### 3. Give your agent the usage instructions

The repository includes a copyable [consumer skill](.agents/skills/graphify-csharp/SKILL.md).
It teaches an agent how to start a warm session, use exact symbol IDs, inspect
callers and relationships, interpret static-analysis limits, and export JSON
when needed. Installing the NuGet package installs the executable; it does not
install a skill.

Project-local Codex setup:

```bash
mkdir -p .agents/skills/graphify-csharp
curl -fsSL \
  https://raw.githubusercontent.com/zachsaw/graphify-csharp/main/.agents/skills/graphify-csharp/SKILL.md \
  -o .agents/skills/graphify-csharp/SKILL.md
```

For a personal Codex installation, use `~/.codex/skills/graphify-csharp/`.
For Claude Code, use `.claude/skills/graphify-csharp/` in a project or
`~/.claude/skills/graphify-csharp/` for a personal installation. Reload the
agent after installing or updating the file.

Without a skills system, put this in the repository's agent instructions:

> For C# structure and usage questions, use the `graphify-csharp` CLI. Identify
> declarations by their exact `symbol_key`/node ID and inspect incoming
> `calls`, `references`, `inherits`, `implements`, and `overrides` relationships.
> Treat zero inbound edges as zero observed static references, not proof of
> runtime unreachability.

## What an agent can ask

- What calls this exact overload or constructor?
- Which declarations reference this field, property, event, type, or enum member?
- Which classes inherit from this type or implement this interface?
- Which overrides satisfy this virtual or interface member?
- Which arguments bind to which formal parameters?
- Which declarations have zero observed inbound static references?
- Which callers originate from projects or namespaces named `Tests`?
- Which declarations are candidates for a test-only usage report?

The last two are analysis questions, not hard-coded classifications. The tool
returns caller provenance; the agent applies the naming convention you choose
and reports the scope and caveats.

## Warm semantic navigation

For repeated agent work, keep a Roslyn workspace warm. `watch` is a foreground,
output-free session; it does not write JSON. Run it in one terminal:

```bash
graphify-csharp watch
```

If discovery is ambiguous, specify the input and root:

```bash
graphify-csharp watch \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release
```

The watcher prints a full session ID and resolved analysis context on stderr
while it starts. In another terminal, use that ID (or a unique prefix):

```bash
graphify-csharp ps --json
graphify-csharp info <session-id> --json
graphify-csharp query symbols OrderService --instance <session-id> --kind class --json
graphify-csharp query callers --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query usage-summary \
  --instance <session-id> \
  --kind method \
  --group-by project,namespace \
  --json
```

Use `symbols` first when a name has overloads. Then pass the exact returned
symbol ID to `signature`, `callers`, `usages`, `hierarchy`, or `arguments`.
Queries return bounded JSON pages with the evidence snapshot used to answer
them. A live session is also useful for edits: queries, exports, and explicit
refreshes wait for the session's startup/recovery barrier.

When the agent needs a complete graph, request it explicitly:

```bash
graphify-csharp export --instance <session-id>
```

This writes the default `./graphify-out/csharp.json` under the calling
terminal's current directory. Choose another destination with `--output`.
The watcher does not publish a new JSON file merely because a source file
changed; export is the explicit publication request.

Useful lifecycle commands:

```bash
graphify-csharp refresh --instance <session-id>
graphify-csharp refresh --instance <session-id> --rebuild
graphify-csharp diagnostics <session-id> --output ./graphify-out/diagnostics.json
graphify-csharp stop <session-id>
```

`refresh` updates trusted in-memory evidence without creating a graph file.
`--rebuild` invalidates reusable contributions for that refresh. `stop` targets
only the selected session; multiple sessions can run at once.

## One-shot queries

You do not need a watcher or a JSON dump for a single question:

```bash
graphify-csharp query callers \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --symbol '<exact-symbol-id>' \
  --json
```

Cold queries require explicit `--input`. They do not create a persistent
session, output file, cursor, or snapshot. A selected `--instance` never falls
back to cold analysis if that session is missing.

## The JSON shape

The complete export is deliberately simple to consume. This is an abridged
shape; IDs and symbol keys are generated from the actual Roslyn declarations:

```json
{
  "nodes": [
    {
      "id": "cs_<stable-node-id>",
      "label": "OrderService.Submit(int)",
      "properties": {
        "node_kind": "method",
        "symbol_key": "csharp/v1|...",
        "project": "src/Orders/Orders.csproj",
        "target_framework": "net10.0"
      }
    }
  ],
  "edges": [
    {
      "source": "cs_<caller-node-id>",
      "target": "cs_<stable-node-id>",
      "relation": "calls",
      "source_file": "src/Orders/OrderController.cs"
    }
  ]
}
```

`calls`, `references`, `inherits`, `implements`, and `overrides` are compiler-
resolved relationships. Locations identify where the relationship was
observed. Invocation and constructor arguments can also be mapped to their
source formal parameters.

## Graphify is optional—and a separate tool

`graphify-csharp` does not invoke, load, or require the separate `graphify`
executable. You can use the CLI and JSON directly with an agent, `jq`, C#,
Python, or another consumer.

If you use Graphify as well, keep the roles separate:

1. `graphify-csharp` produces the C# semantic document or answers a C# query.
2. The separate `graphify` executable consumes that document for its own
   higher-level graph queries, paths, explanations, clustering, or exports.

```bash
graphify-csharp export \
  --input ./src/MyProduct.sln \
  --root . \
  --output ./graphify-out/csharp.json

graphify query "Which methods call the order service?" \
  --graph ./graphify-out/csharp.json
```

The optional Graphify skill teaches the `graphify` tool. The
`graphify-csharp` skill in this repository teaches this CLI. Install both
alongside one another only when you want both workflows; one does not install
or silently invoke the other.

## Runtime and language support

The package contains two global-tool assets:

| Tool asset | Runtime | Compiler surface |
| --- | --- | --- |
| `net10.0` | .NET 10 | C# 14 Roslyn path |
| `net11.0` | .NET 11 | C# 15 preview Roslyn path |

The install-time `--framework` chooses which executable asset runs. The
optional analysis-time `--target-framework` chooses one target framework when
the analyzed project itself targets multiple frameworks. A single-target
project normally needs no target selector.

## Static-analysis boundary

This is compiler evidence, not a runtime reachability proof. Reflection,
dependency injection, dynamic invocation, native callbacks, generated code
outside the evaluated compilation, and external consumers can create runtime
relationships that do not appear as direct static edges.

Therefore:

- zero inbound edges means zero observed static references in the selected
  scope;
- test-only usage depends on the project/namespace convention applied by the
  agent; and
- a deletion candidate still needs review of entry points, reflection, DI,
  source generators, public API consumers, and build/test behavior.

Unsupported or unrepresentable semantic shapes are reported as diagnostics
where possible instead of crashing the entire extraction.

## Development

To contribute to the indexer itself:

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

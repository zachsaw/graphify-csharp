---
name: graphify-csharp
description: Use the installed graphify-csharp CLI for compiler-resolved C# declarations, callers, references, inheritance, implementations, overrides, argument bindings, and bounded semantic queries.
---

# Use Graphify C# on a codebase

This skill teaches the consumer workflow for the installed
`graphify-csharp` executable. It does not install the executable and does not
teach development of the indexer.

Keep this tool distinct from the separate `graphify` executable:

- `graphify-csharp` loads C# with MSBuild/Roslyn, answers semantic queries, and
  explicitly exports a complete JSON document;
- `graphify` consumes a graph document for its own general graph queries,
  paths, explanations, clustering, or exports.

The `graphify` skill and executable are optional. Installing or using one does
not install or silently invoke the other.

## Choose a route

Use a disk export when a complete Graphify-compatible JSON document is needed.
Use a cold `query` when one bounded answer is needed. Use a foreground
`watch` session when several questions or edit/refresh cycles will follow.

Before a current-source answer, choose the solution/project and configuration
that actually define the scope. Include test projects when investigating
test-only callers. If `--input` is omitted for `export` or `watch`, the CLI
looks only in the immediate root: one `.sln`/`.slnx` wins, otherwise one
`.csproj`; ambiguity is an error. It does not recursively discover a project or
auto-select a lone `.cs` file. Explicit file-based `.cs` input is supported.

## Install the executable

```bash
dotnet tool install --global Graphify.CSharp --framework net10.0
```

Use the `net11.0` tool asset for C# 15 preview input when the matching .NET 11
SDK is installed:

```bash
dotnet tool install --global Graphify.CSharp --framework net11.0
```

If it is already installed, use `dotnet tool update` with the same
`--framework` instead.

The install-time `--framework` chooses the tool runtime/compiler asset. The
optional analysis-time `--target-framework` selects one target framework when
the analyzed project itself is multi-targeted. A single-target project usually
needs no target selector.

## Export a complete document

Explicit disk export:

```bash
graphify-csharp export \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
```

From a directory containing one discoverable solution/project, these are disk
export aliases:

```bash
graphify-csharp
graphify-csharp export
graphify-csharp Product.sln
```

The default output is `./graphify-out/csharp.json` under the caller's current
directory. Explicit relative `--input`, `--root`, `--output`, `--path`, and
`--project` values are also relative to the caller's current directory;
`--root` controls analysis scope and discovery, not path rebasing.

The output is one complete JSON document with `nodes`, `edges`, and
`hyperedges`. It is written atomically. Use `--rebuild` to bypass reusable
project contributions and `--json` to receive one structured command result on
stdout. Progress stays on stderr; use `--no-progress` to suppress it.

## Query semantic evidence

Cold query, without a persistent worker or JSON export:

```bash
graphify-csharp query symbols Submit \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --kind method \
  --json
```

Cold queries require explicit `--input`; they do not use input discovery and do
not support cursors or snapshots. A query routed with `--instance` never falls
back to cold analysis if the selected session is missing or incompatible.

For a warm session, first find a declaration and then use its exact ID:

```bash
graphify-csharp query symbols Submit --instance <session-id> --kind method --json
graphify-csharp query signature --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query callers --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query usages --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query hierarchy --instance <session-id> --symbol <symbol-id> --direction implementations --json
graphify-csharp query arguments --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query usage-summary --instance <session-id> --kind method --group-by project,namespace --json
```

`symbols` and `usage-summary` accept a bounded case-insensitive substring.
Exact queries require a symbol ID so overloads, generic members, constructors,
and interface/override contracts are not confused by a spelling match.

Responses include bounded items, page state, the evidence snapshot/scope,
project/namespace/TFM provenance, source locations, and diagnostics. Continue
a live query with `page.next_cursor` using the same verb, symbol, filters, and
direction. A later evidence revision or recovery invalidates cursors and
snapshots with `stale_snapshot`.

`usage-summary` returns fixed inbound call/reference/hierarchy counts and
optional origin groups. It does not label code dead or test-only. For a
test-only audit, inspect the exact declaration's incoming `calls` and
`references`, include `implements`/`overrides` context, and classify each
caller by the repository's project or namespace convention (for example,
`Tests`). Report the scope and static-analysis caveats.

## Start and use a warm session

`watch` is a foreground, output-free session. It never accepts `--output` and
does not write JSON:

```bash
graphify-csharp watch
```

Use explicit analysis settings when discovery is ambiguous:

```bash
graphify-csharp watch \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release
```

The startup progress line on stderr contains the full session ID and resolved
input/root/configuration/TFM context before expensive loading completes. After
readiness, the foreground output shows concrete query/export examples. In
another terminal:

```bash
graphify-csharp ps --json
graphify-csharp info <session-id-or-unique-prefix> --json
graphify-csharp query callers --instance <session-id> --symbol <symbol-id> --json
```

Every watcher invocation creates a new session. No command attaches by matching
input, configuration, root, or output. Use a full ID or a prefix that matches
exactly one session when multiple workers are running.

File events and the backup inventory can update in-memory evidence, but do not
publish JSON. Queries, refreshes, and live exports wait through startup and
recovery barriers:

```bash
graphify-csharp refresh --instance <session-id>
graphify-csharp refresh --instance <session-id> --rebuild
graphify-csharp export --instance <session-id>
```

`refresh` returns a small acknowledgement and no graph file. `--rebuild`
invalidates reusable contributions for that refresh. `export --instance` writes
the current session to the caller's default output, or to `--output PATH`.
Export is the explicit JSON publication request; it is not performed as a side
effect of a query.

## Inspect and stop sessions

```bash
graphify-csharp ps --json
graphify-csharp info <session-id-or-unique-prefix> --json
graphify-csharp inspect <session-id-or-unique-prefix> --json
graphify-csharp diagnostics <session-id-or-unique-prefix> \
  --output ./graphify-out/graphify-csharp-diagnostics.json
graphify-csharp stop <session-id-or-unique-prefix> --json
```

`info` and `inspect` are aliases. They read bounded descriptors and local IPC
state without loading MSBuild, building the semantic index, or taking a fresh
blocking resource sample. They expose lifecycle/readiness, active stage,
evidence counts, last operation, recovery, and cached process metrics.

The registry is a discovery hint. A crashed process can leave a stale record;
the client validates process identity and never kills a PID by name or stale
descriptor. `stop` requests graceful shutdown of the selected session. There is
no `stop --all`.

`diagnostics` is written by the requesting CLI after it receives the bounded
response. The watcher does not write the report path, and an existing
destination is rejected. Reports contain paths, runtime identity,
stage/timing history, evidence counts, recovery history, and process metrics;
they do not contain source contents or a heap dump and are not anonymized.

## Read the complete JSON

Join edges to nodes by ID. Names and labels alone are not safe keys:

- `properties.symbol_key` is the project/TFM-aware semantic identity;
- `properties.node_kind`, `declaration_kind`, `namespace`, `project`, and
  `target_framework` describe the declaration;
- `calls` points from caller to callee, including constructors;
- `references` points from a referencing declaration to a referenced source
  declaration;
- `inherits`, `implements`, and `overrides` describe type/member contracts;
- edge locations identify where the relationship was observed; and
- argument evidence can map an invocation expression to a source formal
  parameter.

Example selection:

```bash
jq '.nodes[] | select(.properties.node_kind == "method")
  | {id, label, source_file, source_location, properties}' \
  graphify-out/csharp.json
```

The output is static evidence for the selected evaluated compilation. Zero
inbound edges means zero observed static references in that scope, not proof
that a declaration is unreachable. Reflection, dependency injection, dynamic
invocation, native callbacks, external consumers, and generated code absent
from the compilation can create runtime relationships that are not represented.

## Optional Graphify workflow

If the separate `graphify` executable is installed, run this tool first and
pass its complete document explicitly:

```bash
graphify-csharp export --input ./src/Product/Product.sln --root .
graphify query "Which methods call the service?" \
  --graph ./graphify-out/csharp.json
```

Do not use `graphify query` as a synonym for `graphify-csharp query`. The first
is the optional document consumer; the second is this tool's compiler-bound C#
query interface. Keep the two skills side by side only when both workflows are
wanted.

## Input and watcher correctness

The watcher follows evaluated MSBuild/Roslyn membership, not `.gitignore` or a
hard-coded directory-name blacklist. It covers evaluated source/additional
documents, project/solution/import/restore inputs, linked files, and candidate
glob rules. Output/intermediate roots are pruned only when evaluated policy
proves them irrelevant; an explicitly included generated source remains
eligible.

Native watcher errors, queue overflow, missing roots, or incomplete backup
scans trigger cold recovery. A restarted watcher creates a new session and
re-establishes trust. If an input change affects project membership or
dependencies, the worker reloads the evaluated workspace rather than guessing
from directory proximity.

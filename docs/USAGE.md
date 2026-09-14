# Usage

The incremental indexing, watcher, refresh, and cache-rebuild behavior is
described in [Incremental indexing and refresh design](INCREMENTAL_INDEXING.md).

## Install

The release artifact is a .NET global tool:

```text
dotnet tool install --global Graphify.CSharp --framework net10.0
```

For C# 15 input, select the .NET 11 tool asset from the same package:

```text
dotnet tool update --global Graphify.CSharp --framework net11.0
```

To build and run the current checkout instead:

```text
dotnet run --project src/Graphify.CSharp.Cli --framework net10.0 -- --help
dotnet pack src/Graphify.CSharp.Cli --configuration Release
```

## Agent setup

Install the executable using the NuGet command above. Then copy the
[consumer skill](../.agents/skills/graphify-csharp/SKILL.md) into the agent's
skill directory. The skill is a self-contained Markdown guide for using the
installed tool on the repository being analyzed; no extractor source checkout
or contributor instructions are needed.

Choose the location that fits your workflow:

| Agent | Project-local directory | Personal directory |
| --- | --- | --- |
| Codex | `.agents/skills/graphify-csharp/` | `~/.codex/skills/graphify-csharp/` |
| Claude Code | `.claude/skills/graphify-csharp/` | `~/.claude/skills/graphify-csharp/` |

For example, install the usage skill for Codex across your projects:

```bash
mkdir -p ~/.codex/skills/graphify-csharp
curl -fsSL \
  https://raw.githubusercontent.com/zachsaw/graphify-csharp/main/.agents/skills/graphify-csharp/SKILL.md \
  -o ~/.codex/skills/graphify-csharp/SKILL.md
```

Use the corresponding directory for Claude Code or a project-local copy.
Reload an agent session after installation. To update an older skill, replace
its `SKILL.md`; updating the NuGet tool does not update copied skills.

Graphify's general skill and executable are optional, separate installations.
When using both tools, the C# skill teaches the refresh and semantic-evidence
steps before Graphify consumes the JSON. Development rules for the extractor
live in this repository's [AGENTS.md](../AGENTS.md); do not copy them into an
application merely to use the tool.

## Query semantic evidence

Use the query commands when the agent needs a bounded answer rather than the
complete Graphify document. They use the same Roslyn-backed workspace and are
available with or without Graphify:

```text
# Start a warm semantic worker. No JSON output or output lease is created.
graphify-csharp \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --watch

# Find the session ID, then choose an exact overload.
graphify-csharp ps --json
graphify-csharp query symbols Submit --instance <session-id> --kind method --json

# Use the selected node ID for exact navigation and call-site questions.
graphify-csharp query signature --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query callers --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query usages --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query hierarchy --instance <session-id> --symbol <symbol-id> --direction implementations --json
graphify-csharp query arguments --instance <session-id> --symbol <symbol-id> --json
```

The query verbs are `symbols`, `signature`, `usages`, `callers`, `hierarchy`,
`arguments`, and `usage-summary`. `symbols` and `usage-summary` accept an
optional positional case-insensitive substring search. The other verbs use an
exact graph node ID, so overloads and generic instantiations cannot be confused
by a matching name. `usage-summary` returns fixed counts for inbound calls,
references, inheritance, implementations, and overrides. Add
`--group-by project,namespace` for flat origin groups; it does not classify
results as dead or test-only.

For a summary, `edge_count` counts distinct merged relationships while
`occurrence_count` counts their source locations; a relationship with no
location counts as one edge and zero occurrences. Scope filters select target
declarations for `symbols` and `usage-summary`, origin/caller declarations for
`usages` and `callers`, returned neighbors for `hierarchy`, and caller
documents for `arguments`.

Each response is a bounded JSON object when `--json` is supplied. It includes
the snapshot, selected scope, diagnostics, and page state. Use the returned
`next_cursor` with the same verb, symbol, filters, and direction to continue a
live-watcher query; the limit may change:

```text
graphify-csharp query callers \
  --instance <session-id> \
  --symbol <symbol-id> \
  --limit 100 \
  --json
graphify-csharp query callers \
  --instance <session-id> \
  --symbol <symbol-id> \
  --cursor <next-cursor> \
  --limit 250 \
  --json
```

An evidence revision or trust-loss recovery invalidates cursors and pinned
snapshots with a structured error. Cold queries use the same handlers without
starting a persistent worker, pipe, or JSON export:

```text
graphify-csharp query symbols Submit \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --kind method \
  --json
```

Cold results do not support `--cursor` or `--snapshot`. If a cold result is
truncated by the response limit, it reports that continuation is unavailable;
narrow the filters or use a watcher. A query sent to an explicit `--instance`
never falls back to cold analysis when that session is missing or incompatible.
Argument spans use Roslyn text offsets in UTF-16 code units. Line and column
locations are one-based and repository-relative. A successful response is
current for the worker's accepted, indexed evidence snapshot; it does not
promise that a disk edit occurring after that event boundary has already been
indexed.

The `query` command is distinct from the optional `graphify` executable and
from `graphify query`: the former asks this tool for compiler-bound C# facts,
while the latter consumes a complete graph document. To create that document
from a warm worker, use explicit export:

```text
graphify-csharp export \
  --instance <session-id> \
  --output ./graphify-out/csharp.json \
  --json
```

Export writes one complete Graphify-compatible JSON file only when requested.
It does not change the query-only worker into an output-backed watcher or
reserve the export directory. A query-only worker has no output-derived
persistent cache; its descriptor is only a discovery hint and may remain as a
stale record after an abnormal exit. For the legacy output workflow, start the
watcher with `--output` as shown below; the ordinary no-subcommand invocation
then refreshes that canonical output and retains its existing `not_ready`
behavior during startup or recovery.

## Extract a repository

Use a repository-relative root so symbol keys and source files do not depend on
the machine’s absolute path:

```text
graphify-csharp \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
```

The first run performs a cold reconciliation and stores internal contribution
state under `.graphify-csharp/` beside the output. Later one-shot runs compare
project/source fingerprints, reuse unchanged project contributions, and still
write one complete Graphify document. To intentionally invalidate that state:

```text
graphify-csharp \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json \
  --rebuild
```

The cache contents are an implementation detail and are safe to discard. Stop
the watcher before deleting its `.graphify-csharp/` directory because that
directory also contains live lease files; alternatively use `--rebuild`. A
missing, incompatible, corrupt, or incomplete cache causes a cold extraction;
it is never treated as evidence for a partial graph.

## Keep a warm watcher

For repeated refreshes, start one watcher for the selected input identity:

```text
graphify-csharp \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json \
  --watch
```

The watcher subscribes before its cold startup, keeps the loaded Roslyn
workspace alive, and queues file-system paths without doing extraction in an
OS callback. It may index dirty projects in the background, but ordinary file
changes do not publish a new JSON document. Run the normal command when a
consumer needs a fresh snapshot; it connects only to a watcher with the
matching analysis configuration and exact output path. If that watcher is
still starting or recovering, the command returns a `not_ready` error and does
not write JSON; retry after `inspect` reports `ready: true`:

```text
graphify-csharp \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
```

If there is no live matching watcher, the normal command performs a cold
one-shot refresh. This includes the case where a watcher is warm for the same
project but was started with a different output path: the request is never
silently redirected to that watcher's file. Separate output files have
output-specific cache and lease state, so they can be refreshed independently.
The canonical output destination is still exclusive: if a live watcher owns
that exact output with a different input, configuration, or target framework,
the command fails with an ownership conflict instead of overwriting the
watcher's graph. Use another output path or stop the watcher. A standalone
refresh holds the same destination lease for its complete operation.
`--rebuild` forces a full cache-invalidating extraction in either mode. The
watcher keeps an OS file watcher for low latency and runs an independent
metadata inventory scan every five minutes by default. Change the interval at
startup with `--watch-scan-interval 00:02:00`. Watcher errors, native-buffer
overflow, bounded-queue overflow, missing roots, or an incomplete backup scan
invalidate the session; subscriptions are recreated and a cold reconciliation
completes before the watcher becomes healthy again. The previous complete JSON
remains readable while recovery runs, and recovery does not delete user files.
Stop the watcher with Ctrl-C, or use the management commands below.

### Discover and manage watchers

Watcher management is local to the current OS user and does not load a project
or Roslyn. List sessions across repositories with:

```text
graphify-csharp ps
graphify-csharp ps --json
```

Each session has an opaque `session_id`. Use the full ID, or a prefix only when
it is unique, to inspect or gracefully stop one watcher:

```text
graphify-csharp inspect <session-id> --json
graphify-csharp stop <session-id> --json
```

`inspect` reports the configured input, root, configuration, selected TFM,
output, process identity, reachability, lifecycle state, readiness, and
generation counters. It remains useful while the watcher is starting,
refreshing, recovering, or stopping. `stop` returns success only after the
identified watcher has finished its indexing/publication work and released its
owned resources. A timeout or unreachable endpoint is not treated as a
successful stop; retry or use Ctrl-C in the worker terminal.

The registry is a small per-user discovery hint. A stale or unreachable entry
may remain after a crash and is shown by `ps`; it is not used to terminate a PID.
Do not use a PID or a process-name search as a management selector. Management
does not provide a force-kill or `stop --all` command.

### What the watcher watches

The project and Roslyn workspace are authoritative for input membership; the
watcher does not read `.gitignore` and does not guess that every file beneath a
project directory belongs to the compilation. Evaluated source documents,
additional files, analyzer configuration, project/solution files, evaluated
imports, references, and other discovered inputs are kept as exact paths.

For performance, ordinary traversal prunes only roots identified by evaluated
MSBuild output/intermediate properties, candidate-glob coverage that cannot
admit a descendant, and the tool's exact output/cache paths. A project
directory name is not a filter. An exact evaluated input wins over root
pruning, and an evaluated candidate glob can reopen a root when its
include/exclude rules admit the path. This means a physical generated file
explicitly included from any directory still refreshes correctly while
unrelated generated files beside it are ignored. If input discovery is
incomplete, traversal stays conservative. The same policy is used by the native
watcher and the backup inventory scan. Exact non-source inputs outside the
repository are scanned by path; only source/additional-input roots that need
live coverage add external watch roots, avoiding a recursive watch over SDK or
package installation trees.

Evaluated wildcard inputs are also retained as candidate rules. A new file with
an arbitrary extension that matches an existing `Compile` or `AdditionalFiles`
glob is therefore considered by both the native watcher and the backup scan;
the project is re-evaluated before the change is published. An evaluated
candidate rule whose include/exclude semantics admit a path inside a prunable
root reopens that part of the root. This includes broad custom globs; the rule
does not need to spell out a conventional directory name.

Content edits to an existing evaluated source document use the warm Roslyn
path. A created, deleted, or renamed source, a project-membership change, or
any project/build/dependency input change goes through a complete MSBuild
workspace reload so `Compile`, `Remove`, conditions, linked files, and disabled
default globs remain authoritative. The reload may be broader than the one
file that changed; that is the deliberate correctness boundary.

During a cold reload, the watcher briefly enters a conservative transition
state. It retains known viable coverage and coalesces events in a bounded
journal. Once MSBuild has evaluated the next project boundary, each event is
reclassified against exact inputs, candidate globs, and evaluated output roots;
unrelated build output is discarded, while relevant edits are replayed through
the normal warm/cold path. Once MSBuild/Roslyn has evaluated the new inputs, the
new immutable snapshot is published before extraction continues, and watcher
coverage is extended or replaced from that snapshot. Scans captured against an
old or transitional snapshot are discarded; existing inventory changes,
including newly discovered
exact inputs, are reconciled before a post-load baseline is accepted. This
closes the edit window around project membership changes without resurrecting
deleted external roots.

If auxiliary MSBuild input discovery is incomplete, the watcher reports a
diagnostic and treats the warm view as untrusted for foreground refreshes. The
next refresh performs a cold load and retries discovery; it does not silently
return the incomplete warm graph as current.

To exercise the packaged tool rather than the solution test doubles, run the
repeatable watcher lifecycle check from the repository root:

```text
./scripts/run-watcher-e2e.sh
```

The input may be a solution, solution filter supported by MSBuild, project
file, or SDK file-based `.cs` app. A project input also loads its project
references that MSBuildWorkspace reports. For a file-based app, the SDK
conversion honors `#:sdk`, `#:property`, `#:package`, `#:project`, and
`#:include` directives, then maps generated-document locations back to the
original source files. The SDK and any referenced packages/projects must be
available locally. The TFM selector is optional for single-target projects.
For a multi-target project, specify one TFM; the tool refuses to silently merge
different compilations.

Example file-based app invocation:

```text
graphify-csharp \
  --input ./src/App.cs \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
```

To check repeatability, run the local CLI twice and compare the complete output:

```text
./scripts/check-deterministic-extraction.sh \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release
```

For watcher changes, use the focused tests while iterating and the packaged
end-to-end script before a release boundary:

```text
dotnet test tests/Graphify.CSharp.Tests/Graphify.CSharp.Tests.csproj \
  --configuration Release --framework net10.0 \
  --filter 'FullyQualifiedName~Incremental'
GRAPHIFY_CSHARP_WATCH_E2E_FRAMEWORK=net10.0 ./scripts/run-watcher-e2e.sh
GRAPHIFY_CSHARP_WATCH_E2E_FRAMEWORK=net11.0 \
  GRAPHIFY_CSHARP_WATCH_E2E_TARGET_FRAMEWORK=net10.0 \
  ./scripts/run-watcher-e2e.sh
```

## Read the graph

Nodes contain stable C# properties:

- `symbol_key`: the complete project/TFM-aware semantic identity;
- `namespace`: the containing namespace, or an empty string for the global
  namespace;
- `project`: repository-relative project path; and
- `target_framework`: the compilation’s selected TFM; and
- `declaration_kind`: the Roslyn declaration shape, such as `class`, `enum`,
  `enum_member`, `method`, `localfunction`, `parameter`, `local`, `alias`,
  `label`, `range_variable`, `indexer`, or `union` (with the .NET 11 tool asset).
  Closed hierarchy types additionally carry `is_closed=true`.

Edges point from the source declaration to the referenced declaration. Inspect
incoming `calls` edges to obtain callers and incoming `references` edges to
obtain other referencers. `calls`, `references`, `implements`, `inherits`, and
`overrides` are direct Roslyn evidence; invocation and constructor arguments
also reference their bound source formal parameters.
Locations on each edge explain where the relationship was observed.
Compiler-known entry points are marked on their node as `is_entry_point=true`.

The `graphify_csharp.diagnostics` array reports workspace-load issues and
recoverable declaration or semantic-extraction issues. An unsupported or
otherwise unrepresentable Roslyn declaration or operation is identified with
its kind or affected document and repository-relative source location where
available; unaffected declarations and documents continue to be emitted.

The declaration catalog covers source namespaces, named types, constructors,
methods/operators/local functions, properties/indexers, fields/enum values,
events, parameters, locals, type parameters, aliases, labels, and query range
variables. Unnamed syntax artifacts and compiler-generated implementation
details are not separate graph nodes in v0.1.

This output is evidence for downstream analysis. The enricher deliberately does
not decide whether a caller is a test, whether a target has zero inbound edges,
or whether code is safe to delete.

## Standalone use

Graphify C# does not invoke, load, or require Graphify. The command emits a
complete JSON document that an agent, `jq`, a C# program, or another analysis
tool can consume directly. Graphify is an optional consumer of the same
Graphify-compatible document; install it only when its graph queries, paths,
explanations, clustering, or exports are useful.

## Graphify integration

The file is valid Graphify extraction JSON: it has the base `nodes`, `edges`, and
`hyperedges` arrays, required `file_type`/`source_file` node fields, and the
required edge confidence fields. It also marks itself as directed and
multigraph so Graphify’s raw JSON loader preserves edge direction and parallel
relationships. Generate the file before each Graphify query or export:

```text
graphify-csharp \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json

graphify query "Which methods call the service?" \
  --graph ./graphify-out/csharp.json
```

The semantic node IDs are stable hashes of full C# symbol keys, so this output
is intended to be the authoritative C# semantic extraction for the selected
scope; merging it with a name-only C# extraction requires an explicit ID-join
layer. Graphify’s clustered NetworkX view can normalize multiple relationships
between the same endpoints; retain `csharp.json` when relation-level evidence
matters.
The CLI emits one complete document. If a future workflow shards extraction,
each shard must keep this same envelope and stable IDs. JSON Lines fragments
are not directly compatible. Graphify’s current `merge-graphs` command is for
independent graph sources and prefixes each input’s IDs, so a same-repository
shard workflow needs a deterministic merger that unions stable IDs, preserves
parallel relations, validates endpoints, and then emits one complete document.

## Known limitations

The extractor follows Roslyn-resolved source symbols. It does not claim to
resolve arbitrary reflection strings, DI registrations, function pointers,
P/Invoke, generated code excluded by the project, or host callbacks. Add
consumer-specific roots and policies in downstream analysis, and review static
limitations before acting on zero-inbound-reference results.

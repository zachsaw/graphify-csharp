# Incremental indexing and refresh design

> Status: implemented. This document describes the v0.2 public workflow and
> the internal boundaries that keep repeated C# analysis useful without
> sacrificing MSBuild/Roslyn correctness.

## Decision

The public complete output remains one Graphify-compatible JSON document. It is
created only by an explicit disk export or live-session export. Incremental
indexing is the internal mechanism that reduces Roslyn work before a requested
export or semantic query; it is not a set of public JSON shards.

There are two ways to use the indexer:

1. A disk command (`export`, including bare invocation) loads the requested
   input, reuses compatible project contributions where possible, and writes
   one complete document.
2. A foreground `watch` command keeps one Roslyn workspace and semantic index
   alive. It has no output destination. `query`, `refresh`, and
   `export --instance` address it explicitly by session ID or unique prefix.

`refresh` waits for trusted evidence and returns a small acknowledgement. It
does not create JSON. `refresh --rebuild` bypasses reusable contributions but
also does not create JSON. `export --instance` is the explicit publication
boundary for a warm session.

No operation chooses a worker by matching input arguments or output identity.
This is intentional: two sessions may analyze the same input with different
configuration or at different points in time. Session selection is explicit.

## Why the watcher is a worker

Loading an MSBuild workspace and producing Roslyn compilations is a significant
part of extraction time. A process that only records file events would not
avoid that cost. A watcher therefore retains, for the lifetime of its session:

- the loaded solution and project graph;
- Roslyn solutions and compilations where retention is safe;
- project/target-framework contributions;
- the evaluated input snapshot and candidate rules;
- dirty paths and dirty projects;
- event and indexed generations; and
- bounded observations for progress, management, and diagnostics.

File events are accepted quickly and handled by worker-owned queues. Background
work can reconcile dirty projects in memory, but it does not write a public
JSON document merely because a file changed. A foreground query/refresh/export
promotes or waits for the relevant work at a safe worker boundary.

```text
file event
    -> bounded event queue
    -> dirty path/project set
    -> optional background reconciliation

query / refresh
    -> trusted evidence barrier
    -> bounded semantic response

export
    -> trusted evidence barrier
    -> merge/validate/serialize
    -> atomic complete JSON replacement
```

One worker owns mutable session state. Requests do not start nested workspace
loads, and a foreground operation is not interrupted halfway through a Roslyn
project operation. Several requests can wait on the same worker boundary.

## Public command contract

```text
graphify-csharp                         # disk export alias
graphify-csharp export [options]        # independent disk export
graphify-csharp watch [options]          # output-free foreground session
graphify-csharp query <verb> [options]   # cold or explicit-instance query
graphify-csharp refresh --instance ID   # warm evidence barrier
graphify-csharp ps [--json]              # list sessions
graphify-csharp info ID [--json]         # inspect one session
graphify-csharp inspect ID [--json]      # alias of info
graphify-csharp diagnostics ID --output report.json
graphify-csharp stop ID [--json]
```

`export` has two explicitly distinguishable routes:

- without `--instance`, it uses `IncrementalRefreshEngine` independently and
  can discover an input; and
- with `--instance`, it asks the selected warm session to export current
  evidence.

The live route defaults its destination to
`./graphify-out/csharp.json` under the requesting process's current directory,
not the worker's root. `--output` changes that destination. The disk route uses
the same default. All explicit relative filesystem paths are resolved once
against the caller's current directory; `--root` controls analysis scope and
discovery, not path rebasing.

Finite commands can use `--json` for one structured stdout response. Progress
is stderr. `watch` is a foreground process and intentionally has no `--json`
mode. Its startup progress includes the full session ID and resolved context;
the final stdout line includes concrete query/export examples.

## Input membership and discovery

MSBuild and Roslyn define the semantic input set. The watcher does not use
`.gitignore` and does not assign meaning to a directory merely because of its
name. After an evaluated load, the immutable input snapshot records exact:

- source and additional documents;
- analyzer configuration and other evaluated project inputs;
- project, solution, import, restore, and relevant configuration files;
- linked/external inputs that the evaluated project admits; and
- candidate include/exclude rules for evaluated globs.

For omitted disk-export or watch input, the CLI examines only the immediate
resolved root: one `.sln` or `.slnx` wins; multiple solutions are an error; if
there is no solution, one `.csproj` wins; multiple projects are an error. It
does not recurse or auto-select a lone `.cs` file. Explicit file-based `.cs`
input remains supported.

Inventory traversal prunes only exact tool-owned paths and roots identified by
evaluated MSBuild output/intermediate properties when candidate rules prove a
descendant cannot be an input. An evaluated glob can reopen such a root when
it admits the path. This keeps unrelated build output out of the event stream
while retaining a generated source that MSBuild explicitly includes.

The evaluated policy is authoritative after every structural reload. An edit
to an existing evaluated source can use the warm document path. Creation,
deletion, rename, project membership, project-file, import, restore, analyzer,
dependency, or uncertain input changes request a fresh evaluation. The worker
does not invent a Roslyn document from directory proximity.

## Watcher reliability

`FileSystemWatcher` is a low-latency hint source, not a durable change log. OS
buffers can overflow, writes can produce duplicate notifications, and a watch
root can disappear. The implementation surrounds it with a correctness
boundary:

- OS callbacks enqueue typed raw paths and do not perform Roslyn work;
- bounded queues and journals make overload explicit rather than silently
  dropping an event;
- a periodic metadata-first inventory scan backs up event delivery;
- watcher `Error`, queue/journal overflow, missing roots, and failed/incomplete
  scans invalidate the current trusted session; and
- recovery tears down/recreates subscriptions, cold-reconciles the evaluated
  scope, and does not report readiness until the boundary is trusted again.

The backup scan compares cheap filesystem metadata with the last accepted
inventory. It does not hash every source file on every healthy refresh. A
rewrite that preserves both timestamp and size is outside the metadata fast
path; use `refresh --rebuild` when exact content verification is required.

Events observed while MSBuild is replacing the evaluated policy are retained
in a bounded transition journal and reclassified after the new policy is
available. They are not mistaken for delivery loss solely because membership
is temporarily unknown. If reclassification cannot establish a complete
boundary, the session takes the conservative cold-recovery path.

A watcher restart always creates a new session and cold-reconciles. It never
trusts a previous timestamp or event stream just because the process was
restarted quickly.

## Session lifecycle and readiness

Every `watch` invocation creates a new opaque session ID. The management and
semantic endpoints are started and a descriptor is published before expensive
MSBuild/Roslyn loading. Progress then reports the ID, input, root,
configuration, and target-framework selector. The initial startup performs a
cold reconciliation; it may reuse verified persisted contributions but it does
not trust an old live event stream.

The important states are distinct:

- `starting`: endpoints/session are being established or initial work is in
  progress;
- `refreshing`: a worker operation is active; readiness can remain true when
  the current input boundary is trusted;
- `ready`: the session has a trusted evaluated boundary and completed initial
  reconciliation;
- `recovering`: event delivery or input trust was lost and cold recovery is in
  progress; and
- `stopping`/`stopped`: new work is refused while ownership is released.

`ready=true` is a trust/readiness signal, not a promise that the worker is
idle. `query`, `refresh`, and `export --instance` wait through startup and
recovery as bounded requests. If a selected session is missing, ambiguous,
stale, incompatible, or unreachable, the command returns a structured error;
it never silently performs a cold fallback.

The management registry is a per-user discovery hint. A crashed process may
leave a stale descriptor, and a PID can later belong to another process. The
client validates the recorded process identity and endpoint; it never kills a
PID based on a stale record.

## Refresh and query barriers

The session worker serializes all semantic operations. A non-rebuild refresh:

1. waits for a trusted input boundary;
2. promotes or finishes reconciliation through the requested event generation;
3. updates in-memory evidence without building a merged graph solely for the
   acknowledgement; and
4. returns the revision/generation and extracted/reused project counts.

It does not serialize JSON or advance a public output generation. A rebuild
uses the same barrier but ignores reusable project contributions. Cancellation
is carried into the queued operation; a cancelled queued request does not
begin later, and cancellation during a safe worker operation follows the
session's existing recovery semantics.

Queries use the current trusted evidence snapshot. `symbols` and
`usage-summary` select declarations by bounded substring; exact queries use a
symbol ID. `callers`, `usages`, `hierarchy`, `signature`, and `arguments` keep
relationship and source-location detail needed for overloads, generics,
inheritance, overrides, and argument-to-formal-parameter analysis.

Live query cursors and snapshots are bound to the evidence revision. A later
accepted edit, recovery, or evidence replacement invalidates them with
`stale_snapshot`; the client must start a new page sequence.

## Incremental invalidation

The initial invalidation unit is a project/target-framework compilation. This
is deliberately broader than one file because C# binding can change through
partial declarations, overloads, extension methods, interfaces, generated
files, aliases, global usings, and project references.

- a changed/added/deleted source invalidates its project/TFM;
- a changed project invalidates it and reverse project-reference dependents;
- solution/SDK/package/analyzer/generator/Directory.* and file-based-app
  changes invalidate their affected graph or the complete scope when mapping
  is uncertain;
- removed projects lose their contributions; and
- unclassifiable changes force a cold reconciliation.

The backup scanner and event path use the same inventory and invalidation
rules. A healthy watcher does not hash every source file before each query.
Events identify dirty inputs, and the warm workspace reads affected files when
the project is reconciled. Standalone disk export uses cheap metadata and
persisted contribution fingerprints for reuse; `--rebuild` bypasses reuse.

## Export consistency and ownership

An export constructs the complete current graph, validates nodes, edges,
endpoints, diagnostics, and output identity, writes a temporary file, and
atomically replaces the requested destination. A destination lease protects
the transaction. The lease is held for the operation, not used to select a
worker.

The warm session serializes its own exports. Independent disk exports do not
attach to a matching watcher, even when their input arguments look similar.
Two writers targeting the same destination still obey the existing destination
ownership policy; a different destination is independent. A failure before
replacement leaves the previous complete JSON untouched. A complete JSON file
is never treated as evidence that a current refresh succeeded.

## Persisted state

The public output is:

```text
graphify-out/csharp.json
```

For disk exports, internal contribution/cache state is stored alongside the
selected output under `.graphify-csharp/`. A query-only watcher has no output-
derived cache destination; its descriptor and semantic endpoint are held in
the per-user management state directory.

Persisted contributions are domain data, not Roslyn workspace objects. They
include compatibility/request identity, project/TFM contributions, source
fingerprints, diagnostics, and publication metadata sufficient to decide safe
reuse. Roslyn workspaces are rebuilt when a process starts.

Do not manually delete cache or lease state while a writer is running. Use
`refresh --rebuild`, or stop the selected watcher first and then remove only
the task-owned internal state.

## Graphify compatibility

The complete export keeps the existing Graphify-compatible directed multigraph
envelope, stable C# node IDs, parallel relationship detail, source locations,
and diagnostics. Internal contribution files are not Graphify input and are
not public shards. If a downstream workflow wants to use the separate
`graphify` executable, it must consume the explicitly exported complete file:

```text
graphify-csharp export --instance <session-id>
graphify query "Which methods call the service?" \
  --graph ./graphify-out/csharp.json
```

The first command belongs to this tool. The second belongs to the separate
optional Graphify tool. They have separate skills and separate command
protocols.

## Verification requirements

The implementation is qualified with focused unit/integration tests and
package-installed E2Es. Important invariants include:

- clean refreshes reuse work and do not publish JSON;
- accepted edits appear after an explicit refresh/export boundary;
- rebuilds bypass reusable contributions;
- queries/refreshes wait through startup and recovery;
- watcher errors, queue overflow, missing roots, and failed backup scans
  recover conservatively;
- project membership, generated-source globs, linked files, and ignored
  evaluated output roots follow MSBuild policy;
- multiple same-input sessions remain isolated;
- no implicit session routing occurs;
- disk and instance export report their actual route and destination;
- JSON publication is atomic and deterministic; and
- diagnostics, management reads, cancellation, stop, and stale-session
  handling remain bounded and race-safe.

Run the packaged checks from the repository root:

```text
./scripts/run-query-e2e.sh
./scripts/run-watcher-e2e.sh
```

Performance investigations should separate workspace startup, Roslyn loading,
project extraction, contribution merging, query-index construction, JSON
serialization, and foreground waiting. Avoid adding timing thresholds that
turn machine variance into a correctness test.

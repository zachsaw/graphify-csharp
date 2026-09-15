# Incremental indexing and refresh design

> Status: the deterministic cache, warm worker, local refresh protocol,
> resilient watcher, and local watcher management are implemented. The watcher policy is intentionally
> conservative at structural and dependency boundaries; the test matrix below
> is the release-hardening contract.

## Decision

The enricher keeps one complete Graphify JSON document as its public output.
Incremental indexing is an internal implementation detail that reduces the
amount of Roslyn work needed before that document is refreshed.

The long-running watcher owns the warm Roslyn state and incremental index. An
output-backed watcher's legacy refresh request is a foreground JSON barrier: it
waits until the requested state has been indexed, serialized, validated, and
atomically published. That legacy request returns a structured `not_ready`
failure while the watcher is starting or recovering; it does not invoke
extraction or modify JSON, and the caller can retry after readiness is observed.
The separate semantic query endpoint waits through startup and recovery and
returns bounded evidence without publishing JSON. Explicit semantic export is
the operation that asks a worker to create a complete Graphify document.

The watcher is trusted only for the lifetime of its current healthy session. A
new or restarted watcher must perform a cold reconciliation before it becomes
ready. A missing watcher never causes an unverified incremental cache to be
used silently.

## Why the watcher is a worker, not only a notifier

Loading an MSBuild workspace and producing Roslyn compilations is a significant
part of extraction time. A process that only records file events would not
avoid that cost. The watcher therefore keeps the following state in memory:

- the loaded solution and project graph;
- the current Roslyn solution and compilations where safe to retain them;
- project/TFM graph contributions;
- dirty paths and dirty projects;
- the current event and index generations; and
- the last successfully published output generation.

The watcher receives file events immediately. It may index dirty projects in a
background queue before a user asks for a refresh, but it does not publish a
Graphify-visible JSON update merely because a file changed. JSON publication is
controlled by a refresh request.

The same rule applies to automatic recovery. Recovery may cold-load the current
workspace and update the in-memory graph so trust can be restored, but it does
not publish JSON or advance the public generation. A later explicit refresh
publishes that recovered state. Initial startup is the exception: it must
publish the first complete document before the watcher reports ready.

This gives us both modes without separate extraction implementations:

```text
file event
    -> dirty project set
    -> optional low-priority background indexing
manual refresh
    -> promote or finish required work
    -> serialize and validate
    -> atomically publish csharp.json
```

## Watcher reliability and reconciliation

`FileSystemWatcher` is the low-latency hint source, not a durable change log.
The .NET implementation documents that its native buffer can overflow and
that the `Error` event is raised when monitoring cannot continue. It also
documents duplicate notifications for ordinary operations such as moves and
writes. The implementation therefore treats event delivery as trustworthy
only when the surrounding session remains healthy.

The watcher combines four mechanisms:

- `FileSystemWatcher` instances use the narrowest practical `NotifyFilter` and
  a bounded native buffer. Their OS callbacks only enqueue raw typed paths; a
  separate dispatch worker resolves the entry type, applies the input policy,
  and invokes the host callback.
- A bounded in-process event queue keeps callbacks off Roslyn and extraction
  work. If the queue is full, the event is not silently discarded: the session
  is marked untrusted and schedules a cold reconciliation.
- A `PeriodicTimer` runs an independent backup reconciliation at a configurable
  interval (initial default: five minutes). It enumerates the configured input
  inventory and compares cheap file metadata with the last accepted inventory.
  Differences become the same dirty-path commands produced by events. The
  scan runs on a worker and never performs Roslyn work in a watcher callback.
- The `Error` handler, missing watch root, failed backup scan, or an actual
  event-queue/journal overflow tears down the watcher and invalidates the
  session. Ordinary events observed while MSBuild is replacing the input policy
  are retained in a bounded transition journal and reclassified after the load;
  they do not become watcher failure merely because membership was temporarily
  unknown. The worker scans the current roots, recreates the subscriptions, and
  performs a nonpublishing cold reconciliation before declaring the session
  healthy again. No user files are deleted as part of recovery.

The backup scan is deliberately metadata-first so it does not turn every
healthy refresh into a full content hash or Roslyn pass. Normal editor saves,
replacements, and deletes change the tracked metadata and are detected without
reading every source file. If the scanner cannot establish a complete boundary
because an input is unreadable, enumeration fails, or a root disappears, the
session loses trust and performs a cold full-scope reconciliation. This version
does not claim to detect an adversarial in-place rewrite that preserves both
size and timestamp; use `--rebuild` when exact content verification is needed.
The fast path therefore gets event latency plus a bounded metadata backstop,
while watcher restart/error recovery always takes the trusted cold path above.

There is no need for a third-party watcher wrapper. The reliability comes from
the queue, explicit error/restart handling, and reconciliation policy around
the .NET primitive. Time-based coalescing is optional for background work and
never substitutes for recording an event or completing a reconciliation.

## Input scope and filtering

MSBuild and Roslyn, not `.gitignore` or directory names, define the semantic
input set. After each evaluated load, the worker publishes an immutable input
snapshot containing exact source documents, additional documents, analyzer
configuration, references, evaluated project/build imports, project/solution
inputs, evaluated item globs, and relevant configuration paths. The same
snapshot also contains output/intermediate roots derived from evaluated
`BaseOutputPath`, `OutputPath`, `OutDir`, `BaseIntermediateOutputPath`,
`IntermediateOutputPath`, `MSBuildProjectExtensionsPath`, and `PublishDir`
properties, plus the evaluated `ProjectAssetsFile` candidate. No global
blacklist of names such as `obj` or `artifacts` is used.

Inventory scans exact evaluated inputs first. It prunes only tool-owned output
and cache paths, roots explicitly identified by MSBuild as generated output or
intermediate state, and directories that candidate-glob coverage proves cannot
contain an input. An evaluated candidate glob can reopen a prunable root when
its include/exclude rules admit that path, so an explicitly compiled generated
file or an arbitrary-extension additional file remains visible. If discovery is
incomplete, the scanner falls back to conservative traversal rather than
silently filtering uncertain paths. A directory is therefore ignored because
the project policy proves it irrelevant—not because of its basename.

An existing evaluated source document changed in place is the only warm
document mutation. Creation, deletion, rename, directory changes, project
membership changes, and non-source input changes request a fresh MSBuild
workspace evaluation; the session never invents a `Document` from directory
proximity. This preserves conditional `Compile`, `Compile Remove`, disabled
default globs, linked files, and nested project boundaries. Exact external
non-source dependencies are protected by the backup scan; live external roots
are added for linked sources and additional inputs where a low-latency event
source is useful, but SDK/NuGet trees are never recursively watched.

When evaluated dependency discovery cannot be completed, the tool remains
usable but marks the input snapshot incomplete and emits a diagnostic. A
foreground refresh takes an authoritative cold path and retries discovery
instead of claiming that the incomplete warm view is current. If discovery
remains incomplete, the output retains the diagnostic so an agent can see the
freshness limitation. Custom target outputs absent from design-time evaluation
remain outside the supported discovery boundary and require the producing step
followed by `--rebuild`.

## Public commands and ownership

The exact command spelling can evolve, but the behavior is:

```text
graphify-csharp ... --watch       # start the long-running worker
graphify-csharp ...               # request a foreground refresh
graphify-csharp ... --rebuild     # request a cache-invalidating rebuild
graphify-csharp ps [--json]       # discover current-user watcher sessions
graphify-csharp inspect <id>      # inspect one session
graphify-csharp stop <id>         # request and confirm graceful shutdown
```

Management commands are client-side discovery plus a small per-session local
IPC endpoint (a named pipe on Windows and a Unix-domain socket elsewhere). They
do not require an input path, load MSBuild/Roslyn, or
read the graph. `ps` reads one bounded descriptor per watcher from the
current-user application-state directory and probes valid records with bounded
timeouts. `inspect` and `stop` accept an exact session GUID or a unique prefix;
an ambiguous prefix fails rather than selecting an arbitrary process.

The descriptor is only a discovery hint and may remain as a stale record after
an abnormal exit. Live state is returned by the worker
and includes reachability, lifecycle/readiness, process-start identity, and
event/index/published generations. A stale PID is never terminated by the
management client. `stop` signals the existing host lifetime owner and reports
success only after indexing/publication work and leases have completed. The
management server keeps the completion response path alive long enough to
reply without awaiting its own disposal, while unrelated `inspect` requests
remain available during a pending stop.

The normal foreground invocation connects to a matching watcher when one exists.
The watcher exposes its refresh channel throughout startup, but accepts legacy
JSON refresh requests only after it is healthy. A request during `starting` or
`recovering` returns `not_ready` without writing JSON; callers should inspect or
retry after the watcher reports `ready`. If no matching watcher exists, the
invocation performs the cold reconciliation itself and waits for completion.

### Targeted semantic queries

Phase 2 adds a separate, current-user local IPC endpoint for bounded semantic
queries (a named pipe on Windows and a Unix-domain socket elsewhere). It shares
the watcher's one Roslyn workspace and worker-owned evidence index,
but it does not require a canonical output path, output lease, cache manifest,
or Graphify installation. Start a query-only worker by omitting `--output`:

```text
graphify-csharp \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --watch

graphify-csharp ps --json
graphify-csharp query symbols Submit --instance <session-id> --kind method --json
graphify-csharp query callers --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query usage-summary --instance <session-id> --kind method --group-by project,namespace --json
```

`symbols` and `usage-summary` select declarations with an optional substring;
`signature`, `usages`, `callers`, `hierarchy`, and `arguments` require an exact
symbol ID. Results are ordered, bounded pages. A live response includes an
opaque snapshot ID and may return a cursor for continuation. Cursors and
snapshots are valid only for the current evidence revision; recovery or a
subsequent evidence replacement returns `stale_snapshot` rather than silently
advancing the request. A query-only worker never creates `csharp.json`.

The same query handlers support a cold one-shot route:

```text
graphify-csharp query symbols Submit \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --kind method \
  --json
```

Cold queries do not create a persistent worker or support cursors/snapshots. A
selected `--instance` is never silently replaced by cold analysis. The
`usage-summary` batch command returns fixed inbound counts and optional flat
origin groups; it deliberately does not decide whether a declaration is dead
or test-only. Consumers apply their own namespace/project convention to the
returned evidence.

When a complete document is required, request it explicitly from a live
worker:

```text
graphify-csharp export \
  --instance <session-id> \
  --output ./graphify-out/csharp.json \
  --json
```

Export is serialized with the session and uses a destination lease. It does not
change which canonical output (if any) the worker owns, and query-only state
remains query-only after exporting elsewhere.

There is one watcher per canonical analysis-and-output identity. The analysis
identity includes the input path, repository root, configuration, selected
target framework, tool and schema versions, and any other option that changes
the compilation. The output identity is the canonical absolute publication
path. A local control channel such as a named pipe or Unix-domain socket is
keyed by both identities and is preferred to a network endpoint.

The output path is therefore part of watcher ownership even though it is not
part of the semantic cache request. A refresh request for the same analysis
with a different output path does not attach to the existing watcher; it falls
back to a standalone refresh (or can use a separately started watcher). The
watcher also validates the request and output identities in the control
message before doing any work. This prevents a successful response from being
reported for a file that the watcher did not write. Cache and lease paths are
output-specific as well, so distinct output files in one directory do not
race through shared incremental state.

A separate destination lease is keyed only by the canonical output path. A
watcher acquires that OS-level file lease before it loads Roslyn and holds it
for its lifetime. A standalone refresh acquires the same lease for its complete
cache/load/extract/publish/save transaction. This makes publication ownership
independent of analysis identity and closes the same-output/different-
configuration race.

The destination sidecar retains the output filename as a filesystem path
component instead of hashing its spelling. Therefore case-equivalent output
and parent-directory aliases share the same OS lease on case-insensitive
volumes while remaining independent on case-sensitive volumes. If an alias
cannot attach to the matching control endpoint, the held destination is
rejected safely rather than written through a second cache identity.

If another process already owns the destination, a request that cannot attach
to that process's matching control channel fails with a clear ownership
conflict. It never performs a standalone refresh against that output. Use a
different output path or stop the existing watcher. Matching requests continue
to use the control channel; different output paths remain independent.

The lease file itself is stable and may remain after a process exits. An
unheld lease file is harmless because ownership is determined by the OS handle,
not by file existence. Keeping the path stable also prevents late cleanup from
deleting a successor's newly acquired lock. Readers can continue to read the
previous complete document while a new one is being built.

## Persistent state

For an output-backed workflow, the public output remains a single file:

```text
graphify-out/csharp.json
```

A query-only worker has no public output or output-derived persistent cache. Its
semantic endpoint and session descriptor live under the per-user management
state directory. A graceful shutdown removes the descriptor; an abnormal exit
may leave a stale discovery record until a later management read observes it.

The watcher may use ignored internal state alongside it:

```text
graphify-out/.graphify-csharp/
    manifest-<output-path-identity>.json
    output-<output-file-name>.lock
    watch-<request-digest>-<output-path-identity>.lock
```

The single manifest contains the persisted project/TFM contributions; it is an
internal cache, not a public Graphify shard file. It contains enough domain data
to reconstruct the complete document, including declarations, edges,
diagnostics, provenance, and each contribution identity. Roslyn workspace
objects are never persisted.

The manifest records at least:

- the canonical request and compilation identity;
- tool, schema, runtime, and Roslyn compatibility versions;
- the project/TFM dependency graph;
- the source-file inventory and last successful fingerprints;
- the contribution digest for each project/TFM;
- the current event, indexed, and published generations; and
- the digest and path of the last successfully published JSON.

Roslyn objects are not persisted as the cache format. They are rebuilt in the
watcher process and discarded when that process ends. Persisted contributions
are domain data and can be checked for compatibility independently.

## Session lifecycle

### Starting or restarting

Every watcher start creates a new opaque session ID. It never resumes a dirty
event queue or trusts a previous session merely because a timestamp appears to
continue from the last update.

The watcher establishes its file-event subscriptions before beginning the
initial load, then performs a cold reconciliation of the complete configured
scope. A cold reconciliation follows the normal project/TFM incremental
contract: it considers every project and input, and may reuse verified
unchanged contributions. It is distinct from `--rebuild`, which ignores those
contributions and extracts every project again.

Events observed while the initial reconciliation is running are retained. The
watcher must process any affected work before reporting the session as ready.
If it cannot establish a trustworthy event boundary, it repeats the cold
reconciliation instead.

Every later cold load has an explicit transition boundary. Before Roslyn
reevaluates the project, the session publishes a bootstrap snapshot that keeps
the current logical input roots conservative. The host keeps the watcher set
that is currently known to be viable; it does not recreate obsolete external
roots merely because they appeared in the previous evaluated snapshot. Events
during this interval are retained in the bounded transition journal and
reclassified after the evaluated policy is available. Only a genuine delivery
loss is folded into cold recovery rather than being treated as a normal edit.

After the load succeeds, the evaluated input snapshot is published before
cataloging and semantic extraction. The host then establishes any newly
discovered coverage and replays the transition journal against the new policy.
A scan captured against an old or transitional snapshot is discarded. The
post-load inventory is accepted only when it matches the current evaluated
snapshot; all differences, including changes to existing entries and newly
observed exact inputs, are reconciled through the serialized cold path before
the new baseline is accepted. During automatic recovery, this catch-up is
nonpublishing and leaves an in-memory publication obligation for the next
explicit refresh. During initial startup, it may publish so the readiness
barrier represents the complete first document. This prevents an input that
changed during the load/coverage gap from becoming a clean but stale baseline.

### Ready and watching

Once the initial complete graph has been published, the session is healthy and
`ready=true`. Readiness describes trusted ownership and an established input
boundary; it does not mean that the single worker is idle. During ordinary
background indexing the management state may be `refreshing` while readiness
remains true. File events add paths to the dirty set and increment the in-memory
event generation. Duplicate events are harmless because the set is keyed by
canonical path or project.

During a cold reload, the session may keep the previously published graph
available while it replaces the evaluated input boundary with a bootstrap
snapshot. That transition is deliberately not ready: `ready=false` remains in
force until the new evaluated snapshot has been reconciled and published. This
prevents a query or refresh from being admitted against an input policy that is
still changing.

An ordinary edit to an existing source document stays in the healthy session.
Low-priority background indexing catches up in memory, and an explicit refresh
issued through the control endpoint is serialized behind that work rather than
rejected merely because the worker is busy. Structural, project, dependency,
watcher-error, or otherwise untrusted changes use the appropriate cold reload
or recovery path. Startup and delivery-loss recovery still withhold readiness
and return `not_ready`; ordinary transition events are journaled and
reclassified rather than treated as delivery loss. If edits continue without a
quiet boundary, indexing remains pending even though the watcher remains
available for serialized refresh requests.

There is no correctness dependency on a time-based debounce. Background work
may coalesce project requests for efficiency, but events are recorded
immediately. A refresh issued through the host API bypasses debounce and waits
for active trust recovery; the external control endpoint instead returns
`not_ready` until the watcher is healthy.

### Stopping or losing trust

The session becomes invalid if the process stops, the watcher reports an error
or overflow, a required watch root disappears, the event queue overflows, a
backup scan fails, or event delivery can no longer be trusted. The current
watcher is torn down and recreated by the worker, but the invalid session must
complete a cold reconciliation before it becomes healthy again. A process
restart always creates a new session and follows the same rule. The next
session must start with a cold reconciliation.

The previous JSON remains a valid last-known snapshot while a replacement is
being built. Automatic recovery does not replace it; it only prepares trusted
in-memory state. An explicit refresh must publish that state before reporting
success.

If a load fails after the transition snapshot is published, recovery validates
the stable base/ancestor coverage but preserves the prior inventory as the
comparison baseline. The post-load scan then detects edits made while watcher
subscriptions were being replaced and retries the cold load. This prevents a
failed transition from erasing changes or leaving the backup scanner spinning
on an untrusted snapshot.

## Invalidation granularity

The initial semantic invalidation unit is a project/target-framework
compilation. This is deliberately broader than a file because C# binding can
change across files through partial types, overloads, extension methods,
interfaces, generated code, aliases, global usings, and project references.

The refresh engine applies these rules:

- a changed, added, or deleted source file invalidates its project/TFM;
- a changed project invalidates that project and its reverse project-reference
  dependents;
- solution files, SDK selection, package restore data, analyzers, generators,
  `Directory.*` build inputs, and file-based-app directives invalidate the
  affected project graph or the complete scope when mapping is uncertain;
- a project that is removed loses its contribution; and
- an unclassifiable change forces a cold reconciliation rather than risking a
  stale graph.

The backup reconciliation uses the same inventory and invalidation rules as
event processing. It is a correctness backstop for event loss, not a second
Roslyn pipeline.

The cache does not need to read every source file during a healthy watcher
session. Events identify dirty inputs, and the warm workspace reads the files
when their projects are rebuilt. A normal standalone refresh uses cheap
filesystem metadata to reuse contributions; `--rebuild` bypasses that reuse and
reads/extracts the complete configured scope when exact content verification is
required.

## Refresh protocol

A refresh request is accepted only while the watcher reports `ready=true` and
captures the current event generation as its target. The watcher then:

1. promotes pending work for the requested generation to foreground priority;
2. finishes or rebuilds the required project/TFM contributions;
3. merges cached and rebuilt contributions deterministically;
4. validates nodes, edges, endpoints, diagnostics, and output identity;
5. serializes the complete Graphify document; and
6. atomically replaces `csharp.json` before returning success.

The control channel returns `not_ready` for a request observed during startup or
recovery. It includes the current lifecycle state and explicitly reports that
no JSON graph was generated. This is a failure of the request, not a fallback
to the previous file or to a second workspace.

If the graph is already clean at the requested generation, the request returns
the existing published generation without loading Roslyn or rewriting JSON.

Several callers can wait on the same refresh generation. They must not start
duplicate workspace loads or duplicate extraction work.

Events arriving after a request's target generation belong to a later
generation. The response can report that newer work is pending; the caller's
success still means that its requested generation was fully published.

## Work priority

Background indexing and foreground refresh use one worker-owned scheduler.
Foreground commands are drained ahead of pending background work, and several
requests for the same generation coalesce. The current implementation waits
for an already-running background reconciliation to reach a safe command
boundary; it does not preempt a Roslyn operation midway through a project.

This logical scheduling policy does not depend on `Thread.Priority`, because
.NET tasks may migrate between threads and thread priority behavior differs by
operating system. Cancellation and safe handoff points remain at project/TFM
boundaries. If a project is already in a Roslyn operation when a manual request
arrives, that operation finishes before the foreground command is handled.

The final serialization and output commit are part of the foreground barrier.
An indexed contribution without a published JSON document does not satisfy a
manual refresh request.

## Rebuild and cache invalidation

`--rebuild` is the explicit cache-invalidation operation. It:

- ignores all persisted project contributions and manifest reuse;
- creates a fresh staging cache;
- extracts every project/TFM in the configured scope;
- validates deterministic output; and
- atomically replaces the complete JSON and cache artifacts independently.

Each artifact is staged and validated before its own atomic replacement. The
JSON and cache are separate files, so they do not have one cross-file atomic
commit: a failure before JSON replacement leaves the previous document in
place, while a failure after JSON replacement can leave a complete new JSON
document alongside the previous cache. The command reports the failure and a
later refresh or `--rebuild` retries the pending work. This operation
invalidates the enricher's cache only; it does not delete unrelated NuGet,
MSBuild, or SDK caches.

If a refresh is requested without a matching live watcher, the standalone
process performs the cold reconciliation described above after acquiring the
destination lease. A matching ready watcher is used through its control
channel; a matching watcher that is still starting or recovering returns
`not_ready` rather than blocking or publishing an unverified graph. An occupied
destination owned by a different request fails with an ownership conflict. The
command must not claim a warm incremental refresh based solely on a persisted
last-update timestamp.

## Output consistency and failure handling

The watcher writes a temporary file in the output directory, validates it, and
uses an atomic replacement. It updates the manifest only after the output
replacement succeeds. If the process fails between those operations, the next
startup detects the incomplete state and performs a cold reconciliation.

The following states are distinct:

- `indexed`: the internal contributions include a generation;
- `published`: the complete JSON includes a generation; and
- `ready`: the watcher has a healthy session and has completed its initial
  reconciliation.

A manual refresh succeeds only when its requested generation is both indexed
and published while the watcher’s delivery-trust epoch is still valid. A
delivery loss racing with extraction causes the request to wait for recovery
and retry rather than return the pre-recovery graph. A failed refresh never
leaves truncated or invalid JSON: failures before JSON replacement leave the
prior complete document untouched, while a later cache-save failure may leave
a complete new JSON document with the prior cache and still reports failure.
The affected work remains pending for a retry or an explicit `--rebuild`. A
background indexing failure marks the warm session untrusted and triggers
nonpublishing cold recovery.

## Graphify compatibility

Graphify receives one complete document at `csharp.json`, with the existing
directed, multigraph, node, edge, hyperedge, metadata, and diagnostic fields.
The watcher’s internal contribution files are not passed to Graphify and are
not a replacement for that document.

This design does not use Graphify’s cross-repository `merge-graphs` command to
combine same-repository cache contributions. That command has different ID and
normalization semantics. The enricher’s own deterministic contribution merge
preserves stable C# IDs and parallel relations before publishing the final
document.

## Verification requirements

The implementation should test the refresh service with a small fixture and a
real project:

- a manual request arriving during a slow healthy background load queues behind
  the active worker and completes without starting a second concurrent load;
- a clean warm refresh returns the existing published generation immediately;
- background indexing and a manual refresh coalesce on one generation;
- a source change, including one observed during automatic recovery, updates
  the published JSON only after manual refresh;
- events arriving during refresh remain pending for the next generation;
- watcher shutdown and restart perform a cold reconciliation;
- watcher errors and event overflow force a cold reconciliation;
- a full event queue, failed backup scan, or missing watch root invalidates the
  session and causes watcher recreation plus cold reconciliation;
- the backup timer detects a source change when no filesystem event is
  delivered;
- `--rebuild` ignores valid cached contributions;
- a failed refresh never leaves truncated or invalid JSON; and
- a different configuration, input, or target framework cannot overwrite an
  output owned by a live watcher, while ownership can be released and
  reacquired safely; and
- repeated equivalent refreshes produce byte-identical complete documents;
- query-only startup reaches readiness without invoking the serializer or
  creating output/cache artifacts;
- semantic queries wait for startup, reuse the warm evidence revision, and
  return stale errors for invalid cursors/snapshots after evidence replacement;
- exact overload, caller, hierarchy, argument, and grouped-summary queries
  agree between cold and warm routes; and
- explicit export is the only semantic-query operation that creates a complete
  Graphify document.

The performance measurement should separate workspace startup, Roslyn
extraction, contribution merging, JSON serialization, and time spent waiting
for the foreground barrier. This tells us whether a later optimization is
worth its added complexity without changing the Graphify output contract.

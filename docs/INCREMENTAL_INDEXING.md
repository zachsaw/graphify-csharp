# Incremental indexing and refresh design

> Status: the deterministic cache, warm worker, local refresh protocol,
> resilient watcher, and local watcher management are implemented. The watcher policy is intentionally
> conservative at structural and dependency boundaries; the test matrix below
> is the release-hardening contract.

## Decision

The enricher keeps one complete Graphify JSON document as its public output.
Incremental indexing is an internal implementation detail that reduces the
amount of Roslyn work needed before that document is refreshed.

The long-running watcher owns the warm Roslyn state, the incremental index, and
JSON publication. A manual refresh is a foreground barrier: it waits until the
requested state has been indexed, serialized, validated, and atomically
published.

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
- The `Error` handler, missing watch root, failed backup scan, or uncertain
  event boundary tears down the watcher and invalidates the session. The
  worker scans the current roots, recreates the subscriptions, and performs a
  nonpublishing cold reconciliation before declaring the session healthy again.
  No user files are deleted as part of recovery.

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

MSBuild and Roslyn, not `.gitignore`, define the semantic input set. After each
evaluated load, the worker publishes an immutable input snapshot containing
exact source documents, additional documents, analyzer configuration,
references, evaluated project/build imports, project/solution inputs, and
relevant configuration paths. Event callbacks only perform canonical path
comparison against that snapshot; they do not evaluate MSBuild, read source
content, enumerate directories, or start subprocesses.

Inventory traversal prunes conventional noise directories (`obj`, `bin`,
`.git`, `node_modules`, `.e2e`, `graphify-out`, `.vs`, `TestResults`, and
`artifacts`). Exact evaluated paths are scanned separately before traversal, so
an explicitly compiled `obj/Generated.cs` or an arbitrary-extension additional
file remains visible without recursively scanning all build output. A root
level custom output is ignored as one exact path rather than hiding its sibling
source files; the cache directory is ignored as a tool-owned subtree.

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
named-pipe endpoint. They do not require an input path, load MSBuild/Roslyn, or
read the graph. `ps` reads one bounded descriptor per watcher from the
current-user application-state directory and probes valid records with bounded
timeouts. `inspect` and `stop` accept an exact session GUID or a unique prefix;
an ambiguous prefix fails rather than selecting an arbitrary process.

The descriptor is only a discovery hint. Live state is returned by the worker
and includes reachability, lifecycle/readiness, process-start identity, and
event/index/published generations. A stale PID is never terminated by the
management client. `stop` signals the existing host lifetime owner and reports
success only after indexing/publication work and leases have completed. The
management server keeps the completion response path alive long enough to
reply without awaiting its own disposal, while unrelated `inspect` requests
remain available during a pending stop.

The normal foreground invocation connects to a healthy matching watcher when
one exists. It waits for that watcher rather than opening a second MSBuild
workspace. If no matching watcher exists, it performs the cold reconciliation
itself and waits for completion.

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

The public output remains a single file:

```text
graphify-out/csharp.json
```

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
roots merely because they appeared in the previous evaluated snapshot. An
uncertain in-scope event during this interval invalidates trust and is folded
into cold recovery rather than being treated as a warm document edit.

After the load succeeds, the evaluated input snapshot is published before
cataloging and semantic extraction. The host then establishes any newly
discovered coverage. A scan captured against an old or transitional snapshot
is discarded. The post-load inventory is accepted only when it matches the
current evaluated snapshot; all differences, including changes to existing
entries and newly observed exact inputs, are reconciled through the serialized
  cold path before the new baseline is accepted. During automatic recovery,
  this catch-up is nonpublishing and leaves an in-memory publication obligation
  for the next explicit refresh. During initial startup, it may publish so the
  readiness barrier represents the complete first document. This prevents an
  input that changed during the load/coverage gap from becoming a clean but
  stale baseline.

### Ready and watching

Once the initial complete graph has been published, the session enters
`Ready`. File events add paths to the dirty set and increment the in-memory
event generation. Duplicate events are harmless because the set is keyed by
canonical path or project.

There is no correctness dependency on a time-based debounce. Background work
may coalesce project requests for efficiency, but events are recorded
immediately and a manual refresh bypasses debounce; it still waits for any
active trust recovery before returning a successful result.

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

If a load fails after the transition snapshot is published, recovery resets
inventory under the stable base/ancestor coverage and retries the cold load.
This prevents a failed transition from leaving the backup scanner spinning on
an untrusted snapshot.

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

A refresh request captures the current event generation as its target. The
watcher then:

1. waits for the initial cold load if the session is still starting;
2. promotes pending work for the requested generation to foreground priority;
3. finishes or rebuilds the required project/TFM contributions;
4. merges cached and rebuilt contributions deterministically;
5. validates nodes, edges, endpoints, diagnostics, and output identity;
6. serializes the complete Graphify document; and
7. atomically replaces `csharp.json` before returning success.

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
destination lease. A matching watcher is waited on through its control channel;
an occupied destination owned by a different request fails with an ownership
conflict. The command must not claim a warm incremental refresh based solely on
a persisted last-update timestamp.

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

- a manual request arriving during a slow cold load waits and does not trigger
  a second load;
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
- repeated equivalent refreshes produce byte-identical complete documents.

The performance measurement should separate workspace startup, Roslyn
extraction, contribution merging, JSON serialization, and time spent waiting
for the foreground barrier. This tells us whether a later optimization is
worth its added complexity without changing the Graphify output contract.

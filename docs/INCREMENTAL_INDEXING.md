# Incremental indexing and refresh design

> Status: design for a future implementation. The current CLI remains a
> one-shot extractor and does not yet provide watcher, refresh, or rebuild
> commands.

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
  a bounded native buffer. Their callbacks do only path normalization and a
  non-blocking write to the worker queue.
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
  cold reconciliation before declaring the session healthy again. No user
  files are deleted as part of recovery.

The backup scan is deliberately metadata-first so it does not turn every
healthy refresh into a full content hash or Roslyn pass. A metadata collision,
unreadable input, inventory enumeration error, or any other uncertainty
escalates to content verification or a cold full-scope reconciliation. If the
timer or watcher cannot establish a complete scan boundary, the session loses
trust rather than claiming that no files changed. This gives the fast path the
performance of events and gives a live session a bounded recovery interval for
missed events; a process restart still requires the full cold-start rule above.

There is no need for a third-party watcher wrapper. The reliability comes from
the queue, explicit error/restart handling, and reconciliation policy around
the .NET primitive. Time-based coalescing is optional for background work and
never substitutes for recording an event or completing a reconciliation.

## Public commands and ownership

The exact command spelling can evolve, but the behavior is:

```text
graphify-csharp ... --watch       # start the long-running worker
graphify-csharp ...               # request a foreground refresh
graphify-csharp ... --rebuild     # request a cache-invalidating rebuild
```

The normal foreground invocation connects to a healthy matching watcher when
one exists. It waits for that watcher rather than opening a second MSBuild
workspace. If no matching watcher exists, it performs the cold reconciliation
itself and waits for completion.

There is one watcher per canonical input identity. That identity includes the
input path, repository root, configuration, selected target framework, tool
and schema versions, and any other option that changes the compilation. A
local control channel such as a named pipe or Unix-domain socket is preferred
to a network endpoint.

Only the watcher or a standalone refresh holding the refresh lock may update
the cache and output. Readers can continue to read the previous complete
document while a new one is being built.

## Persistent state

The public output remains a single file:

```text
graphify-out/csharp.json
```

The watcher may use ignored internal state alongside it:

```text
graphify-out/.graphify-csharp/
    manifest.json
    projects/<stable-project-key>.json
    session.json
```

The internal project files are contribution caches, not public Graphify shard
files. They contain enough information to reconstruct the complete document,
including declarations, edges, diagnostics, provenance, and the contribution
identity.

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

### Ready and watching

Once the initial complete graph has been published, the session enters
`Ready`. File events add paths to the dirty set and increment the in-memory
event generation. Duplicate events are harmless because the set is keyed by
canonical path or project.

There is no correctness dependency on a time-based debounce. Background work
may coalesce project requests for efficiency, but events are recorded
immediately and a manual refresh bypasses any waiting period.

### Stopping or losing trust

The session becomes invalid if the process stops, the watcher reports an error
or overflow, a required watch root disappears, the event queue overflows, a
backup scan fails, or event delivery can no longer be trusted. The current
watcher is torn down and recreated by the worker, but the invalid session must
complete a cold reconciliation before it becomes healthy again. A process
restart always creates a new session and follows the same rule. The next
session must start with a cold reconciliation.

The previous JSON remains a valid last-known snapshot while a replacement is
being built, but it must not be reported as the result of the new refresh.

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
when their projects are rebuilt. During a cold reconciliation, the persisted
manifest can use cheap filesystem metadata as a first pass and escalate to
content inspection or full extraction when the cache cannot be verified.

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
Foreground work is promoted ahead of pending background work, and several
requests for the same generation coalesce.

The implementation should use logical scheduler priority rather than depend on
`Thread.Priority`, because .NET tasks may migrate between threads and thread
priority behavior differs by operating system. Safe yield and cancellation
points belong between project/TFM units. If a project is already in a Roslyn
operation when a manual request arrives, it normally finishes that project and
promotes the remaining work instead of throwing away useful work.

The final serialization and output commit are part of the foreground barrier.
An indexed contribution without a published JSON document does not satisfy a
manual refresh request.

## Rebuild and cache invalidation

`--rebuild` is the explicit cache-invalidation operation. It:

- ignores all persisted project contributions and manifest reuse;
- creates a fresh staging cache;
- extracts every project/TFM in the configured scope;
- validates deterministic output; and
- atomically swaps in the new cache and complete JSON only after success.

The existing output and cache remain available if the rebuild fails. This
operation invalidates the enricher's cache only; it does not delete unrelated
NuGet, MSBuild, or SDK caches.

If a refresh is requested without a healthy watcher, the standalone process
performs the cold reconciliation described above. It must not claim a warm
incremental refresh based solely on a persisted last-update timestamp.

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
and published. A failed extraction leaves the prior complete JSON untouched and
marks the session as requiring a cold rebuild.

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
- a source change updates the published JSON only after manual refresh;
- events arriving during refresh remain pending for the next generation;
- watcher shutdown and restart perform a cold reconciliation;
- watcher errors and event overflow force a cold reconciliation;
- a full event queue, failed backup scan, or missing watch root invalidates the
  session and causes watcher recreation plus cold reconciliation;
- the backup timer detects a source change when no filesystem event is
  delivered;
- `--rebuild` ignores valid cached contributions;
- a failed refresh never leaves truncated or invalid JSON; and
- repeated equivalent refreshes produce byte-identical complete documents.

The performance measurement should separate workspace startup, Roslyn
extraction, contribution merging, JSON serialization, and time spent waiting
for the foreground barrier. This tells us whether a later optimization is
worth its added complexity without changing the Graphify output contract.

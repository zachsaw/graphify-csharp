---
name: graphify-csharp
description: Use the installed graphify-csharp CLI to answer C# callers, references, inheritance, implementations, and usage questions in the user's codebase. Consumer workflow only; not instructions for developing the indexer.
---

# Use Graphify C# on a codebase

This skill teaches use of the `graphify-csharp` executable. It is not the
executable itself and does not install it. All input paths below refer to the
repository being analyzed; no Graphify C# source checkout is needed.

Use it to gather compiler-resolved evidence for the user's task. Developing,
testing, or packaging the indexer is a separate task; do not apply its
maintainer workflow to the analyzed repository.

## Select the input and refresh

Use the repository's existing index command or configuration when available.
Otherwise find the relevant `.sln`, `.slnx`, `.csproj`, or SDK file-based
`.cs` app, and choose the scope/configuration that matches the question.
Include the test projects when investigating test-only usage.

Check `graphify-csharp --help` for the installed command's options. If the
executable is missing, use the published .NET tool when installation is within
the task's scope, or report the missing dependency:

~~~bash
dotnet tool install --global Graphify.CSharp --framework net10.0
~~~

The `net10.0` asset is the C# 14 path. C# 15 preview input needs the
`net11.0` asset and the corresponding runtime/SDK; an existing global
installation can be switched with
`dotnet tool update --global Graphify.CSharp --framework net11.0`.
The analyzed repository's SDKs, workloads, packages, and MSBuild inputs must
also be available.

Refresh the evidence before answering a current-source question; reuse a
successful refresh within the same task while its inputs remain unchanged:

~~~bash
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
~~~

Replace the example input with the actual solution/project path. Paths are
resolved relative to `--root`; keep the same root and configuration across
refreshes. The optional `--target-framework` selects the analyzed compilation
when a multi-targeted input is ambiguous. It is independent of the tool's
install-time `--framework`.

Wait for successful completion before treating the JSON as current. Read
`graphify_csharp.diagnostics` and report gaps relevant to the question.
A failed command may leave a previous complete document on disk; its existence
does not establish a successful refresh.

## Ask targeted semantic questions

For a focused question, use the semantic query interface instead of generating
the complete JSON document. The query interface belongs to `graphify-csharp`;
it is not the separate `graphify query` command. A query-only watcher keeps the
Roslyn workspace warm and creates no graph output:

~~~bash
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --watch

graphify-csharp ps --json
graphify-csharp info <session-id-or-unique-prefix> --json
graphify-csharp query symbols Submit --instance <session-id> --kind method --json
~~~

Use the symbol ID returned by `symbols` for exact queries:

~~~bash
graphify-csharp query signature --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query callers --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query usages --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query hierarchy --instance <session-id> --symbol <symbol-id> --direction implementations --json
graphify-csharp query arguments --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query usage-summary --instance <session-id> --kind method --group-by project,namespace --json
~~~

The available verbs are `symbols`, `signature`, `usages`, `callers`,
`hierarchy`, `arguments`, and `usage-summary`. `symbols` and `usage-summary`
accept a positional case-insensitive substring; the other verbs require one
exact symbol ID. `usage-summary` returns fixed inbound counts and origin
groups. It does not label declarations as dead or test-only; classify those
results using the user's project or namespace convention.

For a summary, `edge_count` counts distinct merged relationships while
`occurrence_count` counts source locations; a relationship with no location
counts as one edge and zero occurrences. Scope filters select target
declarations for `symbols` and `usage-summary`, origin/caller declarations for
`usages` and `callers`, returned neighbors for `hierarchy`, and caller
documents for `arguments`.

Responses are bounded JSON pages. Continue a live query with the returned
`page.next_cursor`, keeping the same verb, symbol, filters, and direction. A
changed evidence revision or watcher recovery invalidates cursors and pinned
snapshots. Cold queries use the same handlers without a persistent worker:

~~~bash
graphify-csharp query symbols Submit \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --kind method \
  --json
~~~

Cold queries do not support continuation cursors or snapshots. If a selected
`--instance` is unavailable, report the routing error; never silently fall back
to a cold analysis. Use `--target-framework` only when the analyzed input has
multiple target frameworks; it selects the compilation and is unrelated to
the global-tool `--framework`.

Argument spans use Roslyn text offsets in UTF-16 code units. Line and column
locations are one-based and repository-relative. A successful response is
current for the worker's accepted, indexed evidence snapshot; it does not
promise that a disk edit occurring after that event boundary has already been
indexed.

## Read the evidence

The output is one complete JSON document with `nodes`, `edges`, and
`hyperedges`. Use local JSON tools or an agent-side query to select relevant
evidence without loading the entire graph into conversational context.

- Join edges to nodes by `id`; `properties.symbol_key` retains the full
  project/TFM-aware identity. Names and labels alone can match multiple overloads.
- `properties.node_kind` selects categories such as `method` or `type`;
  `declaration_kind` gives the more specific Roslyn shape.
- Nodes retain `namespace`, `project`, and `target_framework` in
  `properties`. Use node locations to locate declarations and edge
  `source_file`/`source_locations` to cite the actual usage sites.
- `calls` points from caller to callee, including constructors.
- `references` points from the referencing declaration to the referenced
  symbol, including supported field, enum, property, event, type, and
  compiler-bound argument-to-formal-parameter uses.
- `inherits` points from derived type to base type; `implements` from
  implementing type/member to contract; `overrides` from override to base
  member. Follow incoming edges to find derived types or implementations.

For example, list method identities before choosing an exact target:

~~~bash
jq '.nodes[] | select(.properties.node_kind == "method")
  | {id, label, source_file, source_location, properties}' graphify-out/csharp.json
~~~

For repeated queries, build a node-by-ID map and incoming-edge index once.
Preserve distinct relation kinds and usage locations. Do not edit the raw
extraction to add inferred facts or usage classifications.

## Usage and dead-code questions

Classify callers using the user's namespace/project naming convention. The
CLI supplies evidence; the agent or consumer performs this analysis.

Distinguish direct test-only callers from transitive reachability from tests.
For a method, inspect incoming `calls` and `references` and relevant
`implements`/`overrides` contracts. An interface call can target the
contract while the implementation has no direct call edge. A type can be
used through its members, constructors, or inheritance relationships; counting
only direct calls to the type node is insufficient.

Zero inbound edges means zero observed static references in the selected
scope. Before recommending deletion, account for `is_entry_point` metadata,
compile-time-only uses such as `nameof`, external consumers, test discovery,
reflection, DI, dynamic dispatch, native callbacks, and code absent from the
loaded compilation. Report the scope, convention, evidence, and relevant gaps;
an audit request alone does not authorize deleting code.

## Repeated work with a watcher

For repeated refreshes, start a watcher using the same input, root,
configuration, selected TFM, and output as the ordinary command:

~~~bash
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json \
  --watch
~~~

That output-backed watcher exposes both the legacy JSON refresh channel and
the semantic query endpoint. To keep only semantic state warm, omit `--output`
as shown in the targeted-query section. A semantic query waits for startup and
recovery through its endpoint; the legacy no-subcommand JSON refresh returns
`not_ready` while an output-backed watcher is starting or recovering, and
should be retried after `inspect` reports `ready: true`.

The watcher and cold extraction/query commands print newline-delimited
progress to stderr by default, keeping stdout suitable for machine-readable
results. Use `--no-progress` when the caller owns the terminal or wants quiet
stderr. Progress reports the current stage and real completed work where a
denominator exists. During a host-owned final-inventory or health-barrier gap,
it may report a labelled startup-pending heartbeat. It is not an ETA or a
readiness verdict; a heartbeat proves only that the observation surface is
alive.

Run the ordinary command without `--watch` whenever fresh evidence is needed.
If a matching watcher is still starting or recovering, the command returns a
`not_ready` error immediately and does not write JSON. Retry it after
`inspect` reports `ready: true`. Once it attaches to a ready watcher, the
command waits for the requested generation to be indexed, serialized, and
published; if recovery begins during that accepted refresh, the watcher waits
for a trusted boundary before returning. File events alone do not republish
the public JSON. Without a matching live watcher, the ordinary command
performs a one-shot refresh if the destination is available.

A different analysis configuration targeting an active watcher's output
fails with an ownership conflict. Use another output or stop the relevant
watcher when changing configuration. A different output is refreshed
independently; it is never redirected to the existing watcher's output.

Add `--rebuild` to the ordinary command to invalidate cached contributions.
Stop the watcher before manually removing its `.graphify-csharp/` directory,
which also holds live leases. A watcher restart performs cold reconciliation;
it cannot resume a previously trusted event stream.

Use the built-in management commands to discover and control workers across
repositories for the current OS user:

~~~bash
graphify-csharp ps --json
graphify-csharp info <session-id-or-unique-prefix> --json
graphify-csharp inspect <session-id-or-unique-prefix> --json
graphify-csharp stop <session-id-or-unique-prefix> --json
~~~

Use the exact `session_id` returned by `ps`, or a prefix only when it is unique.
`info` and `inspect` are aliases. They report whether the worker is starting,
ready, refreshing, recovering, or stopping, plus the active stage, loaded
scope/evidence counts, last operation timing, and cached process metrics when
available. A `startup_pending` flag/detail identifies host-owned startup work
when no worker operation is active. They read published state and do not load
the project, build a query index, or capture a fresh blocking resource sample.
`ps` stays compact and adds only the current stage, uptime, and RSS. `stop`
waits for confirmed graceful shutdown; an unreachable or timed-out worker is
not considered stopped. Management does not kill processes by PID, search by
process name, or offer `stop --all`.

When a run needs support investigation, request a bounded report from the
same session:

~~~bash
graphify-csharp diagnostics <session-id-or-unique-prefix> \
  --output ./graphify-out/graphify-csharp-diagnostics.json
~~~

The report is written by the client after it receives the complete local IPC
response; the watcher never writes to that destination. Choose a new output
path if it already exists. The report contains input paths, runtime identity,
stage/timing history, evidence counts, recovery history, and process metrics;
it does not contain source contents, heap dumps, or an anonymization guarantee.
Review it before sharing. An older watcher that does not know `diagnostics`
returns an unsupported-command error; restart it with a compatible version.

The management registry is only a discovery hint. A crashed worker may leave a
stale entry, and `ps` reports that state without deleting it or contacting a
different process that later reuses the PID. These commands are optional when
using Ctrl-C in the watcher terminal.

Membership follows evaluated MSBuild/Roslyn inputs, not `.gitignore`.
Explicitly included generated inputs can be relevant even under `obj/`.
Watcher errors, delivery loss, or failed backup scans trigger cold recovery.
If required generated inputs are absent from design-time evaluation, run their
normal producer workflow as appropriate, then refresh with `--rebuild`.

If starting a detached watcher, retain its PID and log under `graphify-out/`.
Verify process arguments before stopping it and stop only the task-owned
process. Report extraction failures with the command, diagnostics, and affected
scope; changing the indexer's code is not part of using this skill.

## Optional Graphify workflow

`graphify-csharp` and `graphify` are separate executables. This skill works
without Graphify. If Graphify is already part of the user's workflow, refresh
the C# document as above before a Graphify query, path, explanation, or export,
and pass that document explicitly:

~~~bash
graphify query "Which methods call the order service?" \
  --graph ./graphify-out/csharp.json
~~~

Graphify's general skill teaches its graph workflows; this skill supplies the
C# refresh and evidence-reading steps alongside it. Preserve raw
`csharp.json` for audits: a clustered view can normalize parallel
relationships, and name-based graph nodes cannot be joined to semantic IDs
merely by matching labels.

When a complete document is needed from a warm semantic worker, request it
explicitly; this is the bridge to the optional Graphify workflow:

~~~bash
graphify-csharp export \
  --instance <session-id> \
  --output ./graphify-out/csharp.json \
  --json
~~~

Export is not performed implicitly by a targeted query.

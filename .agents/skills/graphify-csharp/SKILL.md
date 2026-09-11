---
name: graphify-csharp
description: Use the Roslyn-backed C# semantic index for compiler-resolved callers, references, declarations, inheritance, implementations, overrides, provenance, and deterministic Graphify output.
---

# Graphify C# enricher

## Outcome

Use this skill when an agent needs reliable C# structure or usage facts. The
published `Graphify.CSharp` tool works standalone and does not require Graphify;
Graphify is an optional consumer that adds traversal, clustering, explanations,
and exports. The output is the evidence layer, not the repository-specific
analysis layer:

- source declaration nodes for namespaces, named types, constructors,
  methods/operators/local functions, properties/indexers, fields/enum values,
  events, parameters, locals, type parameters, aliases, labels, and query
  range variables with stable semantic identity;
- directed `calls`, `references`, `inherits`, `implements`, and `overrides`
  edges resolved by Roslyn, including compiler-bound formal parameters for
  calls and object creation;
- namespace, project, TFM, source-location, and compiler-known entry-point facts;
- C# 14 extension-block receivers/members and paired partial declarations,
  represented with stable scope identity and merged source locations;
- C# 15 collection-expression arguments, union declarations and case-type
  references, closed hierarchies, extension indexers, labeled jumps, and
  memory-safety syntax when the matching .NET 11 tool asset is selected; and
- deterministic Graphify-compatible JSON with diagnostics and provenance.

The loader accepts `.sln`, `.slnx`, `.csproj`, and SDK file-based `.cs` apps.
File-based apps are converted through the installed SDK, with supported SDK
directives and source-location remapping preserved.

Graphify or another consumer can derive callers by following incoming edges and
can classify test-only, production, mixed, or zero-inbound-reference symbols
using its own query/policy. Do not add test-namespace or production-root policy
to this enricher.

## Consumer workflow

For a C# semantic question, refresh the C# evidence before answering. Do not
rely on a name-only graph when overloads, generics, inheritance, extension
members, or cross-project references affect the answer.

```text
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
```

The tool runtime is selected at install time. Use the `net10.0` asset for the
normal C# 14 path and the `net11.0` asset when the analyzed source requires the
C# 15 compiler surface:

```text
dotnet tool install --global Graphify.CSharp --framework net10.0
dotnet tool update --global Graphify.CSharp --framework net11.0
```

The install-time `--framework` selects the runtime/Roslyn asset. The command’s
optional `--target-framework` selects one analyzed project compilation when a
multi-targeted input is ambiguous. These are independent choices. A
single-target project does not need `--target-framework`.

The output is one complete JSON document with `nodes`, `edges`, and
`hyperedges`. Without Graphify, pass it to an agent, `jq`, or another program.
With Graphify, pass it with `--graph` and keep the raw C# document as the
authoritative relation-level evidence.

## Install for an agent

For a personal, machine-wide setup, copy this skill into the matching user
skill directory. Graphify’s general skill is optional; install it only when an
agent also needs Graphify’s query, path, explanation, or export workflow.

Codex:

```text
mkdir -p ~/.codex/skills/graphify-csharp
cp /path/to/graphify-csharp/.agents/skills/graphify-csharp/SKILL.md \
  ~/.codex/skills/graphify-csharp/SKILL.md
```

If Graphify is also installed, add its general skill separately with
`graphify install --platform codex`.

Claude Code:

```text
mkdir -p ~/.claude/skills/graphify-csharp
cp /path/to/graphify-csharp/.agents/skills/graphify-csharp/SKILL.md \
  ~/.claude/skills/graphify-csharp/SKILL.md
```

Claude Code discovers personal skills from `~/.claude/skills` in every
project, and the directory name exposes this skill as `/graphify-csharp`.
For a team-shared setup, copy the skill into the project’s `.agents/skills` or
`.claude/skills` directory and commit it. Reload an already-running agent
session after installing or changing a skill.

## Warm watcher and explicit refresh

For repeated agent work, start exactly one watcher for the selected input,
repository root, configuration, target framework, and output path:

```text
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json \
  --watch
```

The watcher subscribes before cold startup, keeps the Roslyn workspace in
memory, queues file-system hints away from the OS callback, and may prepare
dirty projects in the background. A file event is not a publication signal:
ordinary changes do not silently rewrite the public JSON.

Run the ordinary command when an agent needs fresh evidence:

```text
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
```

That command connects only to a watcher with the same analysis identity and
exact output path. It waits for the watcher’s ready/index/publish barrier and
returns only after the complete document has been atomically published. If no
matching watcher exists, it performs a cold one-shot refresh. A watcher using
another output path is never silently redirected or treated as having written
the requested file.

The canonical output destination is exclusive while any Graphify C# watcher
owns it. A request with a different input, configuration, or target framework
that targets the same output fails with an ownership conflict; it does not
fall through to a standalone write. Standalone refreshes acquire the same
destination lease for their complete cache/load/publish transaction. Use a
different output path or stop the existing watcher when changing analysis
identity.

Use `--rebuild` when cache invalidation is required:

```text
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json \
  --rebuild
```

The watcher runs a backup inventory scan in addition to the OS watcher. Native
watcher errors or buffer overflow, bounded event-queue overflow, missing roots,
and failed inventory scans invalidate the warm session. Recovery recreates the
subscriptions and completes a nonpublishing cold reconciliation before the
watcher becomes healthy again. The prior complete JSON remains readable while
recovery runs; the recovered in-memory graph is published by the next explicit
refresh.
The default backup interval is five minutes; change it only for a concrete
latency need with `--watch-scan-interval 00:02:00`.

Input membership comes from the evaluated MSBuild/Roslyn workspace, not
`.gitignore`. Exact evaluated source, additional, analyzer/configuration,
reference, import, project, and solution paths take precedence over the
conventional `obj/` and `bin/` noise filter. An explicitly compiled physical
source under `obj/` is therefore watched/scanned, while unrelated files in that
subtree are dropped before they reach the session. Exact non-source inputs are
included in the backup scan; linked sources and additional inputs outside the
repository can receive narrow live coverage without recursively watching SDK
or package trees.

Evaluated wildcard rules are retained as candidate filters too. A newly
created arbitrary-extension file matching an evaluated `Compile` or
    `AdditionalFiles` glob is eligible in both the native watcher and backup
    inventory, then causes a cold MSBuild/Roslyn re-evaluation. The ordinary noise
    filter still prunes `obj/` and similar directories unless an evaluated
    candidate rule can reach that path and its own evaluated exclusions do not
    reject it. Broad custom globs can therefore reopen an excluded subtree even
    when they do not spell out its directory name.

Only an in-place edit to an existing evaluated source document uses the warm
document path. New, removed, or renamed files and project/build/dependency
changes trigger a complete MSBuild workspace reload so project globs,
`Compile Remove`, conditions, linked files, and disabled default includes are
authoritative. Do not infer that a file beside a project is compiled, and do
not expect an arbitrary custom-target output absent from design-time evaluation
to appear until its producer has run and a fresh `--rebuild` is requested.

If auxiliary MSBuild input discovery is incomplete, the snapshot carries a
diagnostic and foreground refreshes use the authoritative cold path to retry
discovery. Do not treat an incomplete warm snapshot as proof that no external
input changed.

During a cold reload, the watcher publishes a conservative transition snapshot
while MSBuild/Roslyn reevaluates the project. It retains the currently viable
coverage, treats uncertain in-scope events as cold-recovery evidence, and does
not recreate obsolete external roots from that transitional snapshot. The
evaluated snapshot is published before semantic extraction, and scans captured
against an old or transitional snapshot are discarded. Existing inventory
changes are reconciled before the post-load baseline is accepted.

When starting a detached watcher, retain its PID and log under
`graphify-out/`, and verify the process arguments before stopping it. Do not
kill processes by broad process-name matching.

## Working method

Read the relevant design documents under `docs/` before changing architecture.
Work in small vertical slices:

1. define or preserve a narrow contract;
2. implement the smallest independently testable unit;
3. add high-value tests for the behavior and its failure boundary;
4. run formatting/build/tests and a relevant fixture smoke test;
5. commit the slice before moving on.

Optimize for fast, safe release. Do not add framework, dispatch, or reflection
complexity before direct semantic extraction is useful. This repository does
not require 100% code coverage; it does require focused coverage of identity,
edge direction, serialization, determinism, TFM selection, and representative
Roslyn language constructs.

### Pragmatic fast go-to-market

Treat test depth as a risk decision, not a coverage contest. For a documentation
or help-text change, use a diff check and the smallest relevant smoke check. For
isolated parsing or serialization, add focused unit tests and at least one
failure case. For semantic identity, Roslyn resolution, project/TFM loading,
edge direction, or schema changes, require focused regression tests plus an
end-to-end fixture and a determinism check. These are the areas where a small
bug can invalidate the whole graph.

Run the narrow tests while iterating so feedback stays fast. Run the full suite,
Release build, package/install smoke test, and representative real-solution
determinism check at slice or release boundaries, or earlier when the change has
high blast radius. Do not allow more than one logical slice of unverified
behavior to accumulate. Record deliberately deferred coverage or known dynamic
limitations in the plan/docs instead of silently weakening the contract.

### Performance invariants

Treat extraction cost as part of the design. Prefer Roslyn’s compilation symbol
tree for the declaration catalog, and use targeted syntax queries only for
declarations that are not exposed as type members, such as local functions,
locals, aliases, labels, and query range variables.
Reuse each project’s semantic models and source-location factory; do not reload
or reparse a project per relationship. Walk each Roslyn operation root at most
once per syntax tree and feed observations into a deduplicating edge accumulator
so overlapping operation and syntax evidence does not create a large
intermediate list. Keep fallback symbol matching O(1) on a prebuilt key index
and reject ambiguous matches without broad name scans. Measure the pinned
real-world e2e before and after semantic changes. Keep the project/target-
framework compilation as the semantic boundary, but use a bounded scheduler
with coarse, deterministic batches of source files when profiling shows
parallel extraction is beneficial and Roslyn/MSBuild thread-safety remains
clear. A source file is the smallest scheduling unit; never create work items
per class, declaration, syntax node, or edge, and do not split a syntax tree
merely to increase task count.

## Design rules

### Keep actors small

Use separate components for project loading, symbol identity, declaration
cataloging, direct reference extraction, and Graphify serialization. A component
should have one reason to change and a narrow input/output contract. Avoid
classes named `Manager`, `Helper`, or `Orchestrator` that hide several policies.
Pure records and pure functions are preferred where they make behavior obvious.

Load Roslyn/MSBuild at the boundary. Keep the domain model and output
validation usable with in-memory records and test doubles, without requiring an
IDE, Rider, or a full solution load.

### Identity must be semantic and reproducible

Never identify a C# method by filename plus method name. The key must include:

- normalized project identity;
- target framework when known;
- containing namespace and nested-type path;
- type kind/name and generic arity;
- member name and generic arity; and
- parameter types and modifiers sufficient to distinguish overloads.

Use the full canonical key as extraction evidence. If Graphify’s current node-ID
constraints require a compact ID, derive a stable ID from the key and retain the
full key in node properties. Never use a process-local hash, source line, or
unordered collection to define identity.

Roslyn can expose compiler-generated containing scopes for newer syntax. Do not
feed an empty synthetic name into the domain identity; encode a deterministic
semantic scope segment (and coalesce defining/implementing partial symbols while
retaining all source locations). If a declaration still cannot be represented,
skip only that declaration and surface a stable diagnostic in the Graphify
metadata.

### Edges describe evidence

In v0.1, emit directed edges from source to target:

- `calls` for a Roslyn-resolved invocation or object creation;
- `references` for supported non-call symbol uses such as method groups,
  `typeof`, attributes, enum initializers, declaration headers, and
  type/member references, local/parameter uses, generic constraints, and
  compiler-bound call arguments to source formal parameters;
- `inherits` for a source type’s Roslyn-resolved base class;
- `implements` for a source type/member implementing a Roslyn-resolved contract;
  and
- `overrides` for a source member overriding a Roslyn-resolved base member.

Do not emit or claim `dispatches_to` expansion until a separate conservative
call-target slice adds and verifies it.

Attach relation provenance and source location. Direct semantic facts are
`EXTRACTED`; conservative call-graph expansion is `INFERRED`; reflection or
other unresolved mechanisms are `AMBIGUOUS` or represented by an explicit
external root. Do not create an edge merely because two names look alike.

Deduplicate by `(source, target, relation, location/provenance policy)` and emit
nodes and edges in stable order. Preserve enough detail for a downstream query
to explain why a target has a given set of callers. Do not emit a derived caller
classification here.

### Treat dynamic behavior honestly

Static absence is not proof of runtime absence. Distinguish at least:

- observed static references;
- compile-time-only references such as `nameof` when relevant;
- compiler-known facts such as application entry points;
- unresolved/dynamic evidence; and
- symbols outside the analyzed source boundary.

Do not pretend that a full call graph can resolve arbitrary reflection, DI,
function pointers, P/Invoke, or host callbacks. Make those limits visible
in diagnostics and documentation; leave policy-specific roots to the consumer.
The v1 declaration catalog models source declarations for which Roslyn exposes
a stable, meaningful graph identity, including scoped declarations. Unnamed
syntax artifacts and compiler-generated implementation details remain omitted.

## Verification

For every semantic change, test the smallest contract that changed and at least
one end-to-end fixture when the change crosses Roslyn or Graphify boundaries.
Prioritize:

- overloaded, generic, nested, partial, and multi-project symbol identity;
- C# 15 collection-builder calls, union/case relationships, closed hierarchy
  facts, extension indexers, labeled branch targets, and memory-safety syntax;
- namespaces, class/record/struct/interface/enum/delegate types, enum values,
  constructors/operators/local functions, properties/indexers, fields, events,
  parameters, locals, type parameters, aliases, labels, and query range
  variables;
- caller-to-callee direction and overload resolution;
- cross-project target resolution and inheritance/interface relationships;
- a pinned real-world repository with complex generic and inheritance syntax;
- namespace/project/TFM metadata and compiler-known entry-point facts;
- deterministic ordering and duplicate elimination;
- malformed input and unsupported-language diagnostics; and
- stable Graphify JSON consumed by the real CLI path.

Use the installed/pinned .NET SDK and explicit solution configuration, with an
optional TFM selector that becomes mandatory when target selection is
ambiguous. Do not rely on Rider for correctness or CI; Rider/InspectCode may be
used as an optional independent comparison during investigation.

The published tool is one multi-targeted package. Select its runtime asset with
`dotnet tool install` or `dotnet tool update --framework net10.0` or
`--framework net11.0`; this selection is separate from the input project’s
`--target-framework` compilation selector. Packaging the complete tool requires
both SDK lines, because the C# 15 workspace assemblies are taken from the
matching .NET 11 SDK until a public Roslyn package supplies that surface.

Before a slice commit, run its narrow tests, a build when code compilation is
affected, and `git diff --check`. Before a release or merge milestone, run the
full suite and the release gates described above. Record known limitations
instead of weakening the semantic contract to make a test pass.

## Graphify integration

Follow Graphify’s extractor contract and current schema at the integration
boundary. Keep C# semantic metadata additive and versioned when it cannot fit
the base schema. Use directed mode so downstream consumers can distinguish
caller from callee. Keep extraction deterministic and avoid inventing
relationships to satisfy a visualization.

When this skill is installed in a consuming C# repository alongside Graphify’s
general `graphify` skill, regenerate the C# evidence layer before Graphify
queries, paths, explanations, or exports:

```text
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
```

For an SDK file-based app, pass its `.cs` path instead. The enricher invokes the
SDK conversion path and honors supported `#:` directives before Graphify reads
the resulting complete extraction document.

Pass the resulting file to Graphify with `--graph`. Preserve the raw C# JSON
for audits because it retains directed, relation-level edges. This workflow
does not move repository-specific policy into the enricher; consumers decide
how to interpret namespaces, callers, and compiler facts.

For repeated agent queries, a consuming repository can keep the enricher warm:

```text
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json \
  --watch
```

The agent should continue to invoke the ordinary `graphify-csharp` command
before a Graphify query or export. That command uses the matching local watcher
when available and waits for its foreground publication barrier; without one it
performs a cold refresh. `--rebuild` explicitly invalidates the cache. The
watcher uses `FileSystemWatcher` only as a low-latency hint source, keeps its
callbacks to bounded path enqueueing, and backs them with a metadata inventory
timer. Overflow, watcher errors, missing roots, or an uncertain inventory force
watcher recreation and cold reconciliation. Do not make an agent assume that a
file event alone means the public JSON has already changed. A foreground
request also validates the watcher trust epoch after extraction; if delivery
was lost during that request, it waits for recovery and retries before
returning success.

The CLI currently emits one complete document. If same-repository output
sharding is added later, each shard must retain the envelope and stable IDs;
JSONL fragments alone are not Graphify-compatible. Graphify’s current
`merge-graphs` command is for independent graph sources and prefixes input IDs,
so same-repository shards need a deterministic merger that unions nodes by ID,
preserves parallel relations, validates endpoints, and emits one complete
document.

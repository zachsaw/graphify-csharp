# Graphify C# implementation plan

## Goal

Ship a small, reusable, OSS Graphify C# semantic enricher. Given a C# solution,
project, or SDK file-based `.cs` app, it deterministically emits:

- source declaration nodes for namespaces, named types, type members, enum
  values, local functions, parameters, locals, type parameters, aliases,
  labels, and query range variables with stable, project/TFM-aware symbol
  identity;
- directed edges for Roslyn-resolved calls, inheritance, implementation,
  overrides, formal argument bindings, and non-call references;
- namespace, project/TFM context, source locations, and compiler-known entry
  point facts as node metadata; and
- Graphify-compatible JSON with diagnostics and provenance.

Graphify or another consumer can then answer questions such as “zero inbound
references,” “test-only callers,” or “production callers” by querying the
directed graph and node metadata. This repository does not classify callers or
apply repository-specific test/production policies.

The first release is intentionally narrower than a whole-program runtime
reachability proof. Reflection, dependency injection, native calls, and other
dynamic mechanisms remain visible as limitations of static extraction;
they are not guessed into the graph.

## Delivery principles

- Prefer Roslyn/MSBuild semantic information over text search or IDE-only output.
- Keep the graph model independent from project loading so the important
  behavior is cheap to unit test. Repository-specific analysis policies belong
  downstream, not in this enricher.
- Preserve Graphify compatibility and add metadata only where it improves
  reproducibility or semantic interpretation by downstream consumers.
- Make the common path useful quickly; defer expensive or speculative analysis
  until the direct semantic graph is stable.
- Every slice gets a focused verification gate and a commit before the next
  slice. High-value coverage is required; 100% coverage is not a release gate.

## Contract audit — 2026-09-08

The initial implementation accidentally put the requested repository audit in
the enricher. The following are explicitly removed from the product contract:

| Found in | Violation | Correct boundary |
| --- | --- | --- |
| CLI | `--test-namespace` and `--production-root` selected analysis policy | Keep only compilation and output selection here |
| Core library | `UsageAuditAnalyzer`, `UsageAuditResult`, `CallerUsage`, and `NamespaceTestPolicy` classified callers | Delete the policy layer; expose nodes and directed edges |
| JSON | `graphify_csharp.audit` duplicated callers and emitted classifications | Keep versioned extractor metadata and semantic node/edge facts |
| Docs and skill | Described production/test/mixed/zero-reference results as this tool’s outcome | Describe downstream Graphify queries instead |
| Tests | Asserted namespace policy and audit classifications | Assert extraction direction, identity, metadata, and deterministic output |
| Loader | Omitted TFM became `tfm=unknown` in final symbol keys | Resolve one actual TFM or reject ambiguous multi-target input |
| Contract docs | Declared `implements`/`overrides`/dispatch relations without emitting them | Implement and verify declaration-level `implements`/`overrides`; keep `dispatches_to` deferred |

Compiler-known entry points and namespaces remain extraction facts. A target
framework is a compilation selector, not an analysis policy: it is optional for
single-target projects, but an ambiguous multi-target project must be selected
explicitly so its graph and symbol identity are not silently mixed.

## Slices

### 1. Repository contract and release shape — complete

Deliver:

- this plan;
- the local `graphify-csharp` skill with the repository’s C# and SOLID guidance;
- a minimal README describing the outcome and scope.

Gate: clean patch, valid skill frontmatter, and documented acceptance criteria.

Commit: `docs: define Graphify C# enricher plan and skill`

### 2. Pure domain model and canonical symbol identity — complete

Deliver deterministic records for symbols, relationships, provenance, and
source locations. Canonical keys must distinguish projects, TFMs, overloads,
generic arity, nested types, return-type-sensitive operators, and ref-like
parameter shapes.

Gate: unit tests for identity collisions, edge deduplication, validation, and
stable ordering without Roslyn or MSBuild.

Commit: `feat: add deterministic graph domain model`

### 3. Roslyn project loading and declaration catalog — complete

Deliver a headless loader for an explicit `.sln`, `.slnx`, or `.csproj` and a
catalog of all source declarations that have meaningful graph identities:
namespaces, named types, constructors, methods/operators/local functions,
properties/indexers, fields/enum values, events, parameters, locals, type
parameters, aliases, labels, and query range variables. Record project and
target-framework context; do not silently merge symbols from different target frameworks. The
`--target-framework` selector is optional for an unambiguous single-target
project and required only when a multi-target project cannot be selected
unambiguously.

Gate: fixture solution loads from the CLI/test harness, produces stable
declaration keys with the actual TFM when selection is unambiguous, catalogs
representative declaration kinds, and rejects ambiguous multi-target selection
with an actionable diagnostic.

Commit: `feat: load CSharp projects and catalog symbols`

### 4. Direct semantic references and callers — complete

Deliver extraction of direct invocation/call, method-group/delegate, type,
attribute, enum-initializer, `typeof`, declaration-header, and related
Roslyn-resolved references. Also emit declaration-level `inherits`,
`implements`, and `overrides` edges for source members/types. Resolve source
targets across project compilations using a unique canonical fallback and skip
ambiguous matches. Orient edges from source to target and retain source
locations and relation provenance. Do not infer `dispatches_to` expansion in
this slice.

Gate: focused fixtures cover overload resolution, constructors, interfaces,
virtual methods, inheritance, partial declarations, generics, enum values,
local functions, scoped declarations, and cross-project calls. A pinned
real-world third-party
fixture must also catalog multiple declaration kinds and pass a repeated-run
byte determinism check. The result must be deterministic across repeated runs.

Commit: `feat: extract deterministic CSharp semantic references`

### 5. Graphify serialization and command-line interface — complete

Deliver Graphify-compatible JSON, a versioned metadata envelope where needed,
stable ordering, diagnostics, and a headless command that accepts the solution,
configuration/optional TFM, and output path. Do not expose test namespace or
production-root options; those are downstream analysis concerns.

Gate: golden JSON tests plus a headless smoke test against the fixture solution;
the output contains no derived audit classification, and invalid input produces
actionable diagnostics and a non-zero exit code.

Commit: `refactor: keep analysis downstream of enricher`

### 6. Real-solution determinism and OSS release hardening — complete

Deliver a reusable repeated-run byte determinism check, packaging, usage
documentation, CI for the supported SDK, and a small compatibility matrix. Keep
optional dispatch expansion and advanced reflection heuristics out of the
critical release path unless the direct graph reveals a concrete need.

Gate: clean checkout build/test, package install/run smoke test, pinned
real-world semantic e2e, representative real-solution determinism check, and
documented known limitations.

Commit: `chore: add deterministic extraction release gates`

### 7. SDK-aligned multi-target tool and C# 15 support — complete

Deliver:

- one `Graphify.CSharp` tool package with `net10.0` and `net11.0` tool assets;
- framework-specific Roslyn/MSBuild dependencies so the selected tool asset
  matches the installed SDK and does not downgrade C# 15 input to the C# 14
  compiler surface;
- explicit install and update documentation using `--framework` when needed;
- C# 15 preview fixture coverage for collection-expression arguments, union
  declarations and case references, closed hierarchies, extension indexers,
  labeled jumps, and the preview memory-safety syntax; and
- CI/package smoke checks that install and execute both framework assets.

Gate: .NET 10 tests and package installation remain green, the .NET 11 asset
loads a C# 15 project without a crash, each public-preview feature has a
representative semantic regression, implicit collection-builder calls and
branch-label references are represented, closed types retain compiler facts,
and a repeated package/tool run produces byte-identical output. Missing
SDK/framework support must produce an actionable diagnostic rather than
silently selecting the wrong compiler.

Verification: .NET 10 (39 tests) and .NET 11 (42 tests) pass; the C# 15
fixture produces 89 nodes and 64 edges deterministically; the packed net10 and
net11 assets install and execute successfully; the pinned Dapper extraction
produces 3,656 nodes and 6,960 edges deterministically; and the full
`package-smoke` workflow passes under Docker/`act`.

Commit: `feat: cover CSharp15 semantic feature shapes`

### 8. Complete declaration and compiler-pattern surface — complete

Deliver:

- SDK file-based `.cs` app loading, including source remapping and the
  `#:sdk`, `#:property`, `#:package`, `#:project`, and `#:include` directives;
- explicit operation edges for local and parameter references, formal
  parameters of calls/object creation, property/event accessors, operators,
  conversions, deconstruction, foreach/await/using patterns, interpolated
  string handlers, ranges, patterns, and function-pointer source references;
- declaration coverage for all named source symbols and meaningful scoped
  declarations exposed by Roslyn, with stable diagnostics for unrepresentable
  identity/operation shapes; and
- a performance-safe operation-root traversal that avoids repeatedly walking
  the same Roslyn operation tree.

Gate: focused language-surface and file-based-app tests pass on both supported
SDK assets, unsupported identity/operation exceptions become Graphify
diagnostics, repeated extraction remains byte-deterministic, and the pinned
real-world package e2e remains green.

Verification: the language-surface fixture covers compiler-selected source
members and formal argument bindings; the file-based fixture covers source
remapping, SDK directives, package restore, and project references; and the
real-world gate completes with deterministic Dapper output.

## Incremental indexing implementation plan

This feature is implemented on the `feature/incremental-indexing` branch. The
public Graphify contract remains one complete `graphify-out/csharp.json`; the
incremental index, watcher state, and project contributions are internal
implementation data. Each phase below must pass its stated gate and be
committed before the next phase begins.

### Phase 0 — branch and design baseline — complete

Deliver:

- the `feature/incremental-indexing` branch;
- [the incremental indexing design](docs/INCREMENTAL_INDEXING.md); and
- this phased implementation plan.

The design establishes a warm watcher/indexer, manual refresh barriers,
generation tracking, cold reconciliation after restart, `--rebuild`, logical
foreground/background priority, atomic complete-JSON publication, and no
same-repository public shard format.

Gate: clean branch baseline, design document linked from usage documentation,
and no implementation behavior changed by the planning work.

### Phase 1 — deterministic refresh state and cache primitives — complete

Deliver small, Roslyn-independent contracts for:

- canonical refresh identity (input, root, configuration, TFM, tool/schema);
- source/project fingerprints and manifest entries;
- event, indexed, published, and session generations;
- project/TFM contribution envelopes; and
- atomic, versioned cache read/write with compatibility rejection.

Completed: the Roslyn-independent contracts, deterministic wire envelope,
atomic store, and focused tests are implemented. The focused gate passes on
both `net10.0` and `net11.0`, including schema/request incompatibility,
corrupt/incomplete state, deterministic round trips, generation ordering, and
atomic failure retention.

The cache format must be domain data rather than serialized Roslyn objects.
Stable ordering, explicit schema/version checks, and safe handling of corrupt
or incomplete state are required.

Gate: focused unit tests cover identity, fingerprint comparison, generation
ordering, cache compatibility, corruption, deterministic serialization, and
atomic-failure behavior. No MSBuild or watcher dependency is introduced in
this phase.

Commit target: `feat: add incremental refresh state and cache contracts`

### Phase 2 — contribution extraction and cold reconciliation — complete

Deliver:

- deterministic project/TFM graph contributions containing declarations,
  semantic edges, diagnostics, and provenance;
- persistence and reuse of unchanged contributions;
- cold reconciliation of the complete configured scope;
- project/TFM invalidation for changed, added, and deleted source inputs;
- reverse project-reference invalidation for dependent compilations; and
- atomic reconstruction of the complete Graphify JSON document.

Completed: per-project contribution extraction, complete-scope reconciliation,
cache reuse, reverse dependency invalidation, atomic output, output validation,
and the CLI `--rebuild` switch. Added/deleted sources invalidate their owning
project; changed projects invalidate reverse project-reference dependents.
The full supported-framework suite passes (`51` tests on `net10.0`, `54` on
`net11.0`), and the pinned package/fixture e2e remains deterministic.

Normal one-shot refresh must avoid content reads and Roslyn extraction for
verified unchanged projects where possible. A missing, incompatible, corrupt,
or uncertain cache must fall back safely. `--rebuild` must ignore persisted
contributions and extract every project/TFM in the selected scope.

Gate: existing full tests remain green; focused tests cover changed/unchanged/
deleted projects, dependency invalidation, output completeness, failure
retention of the last valid JSON, and repeated-run byte determinism. A real
fixture must demonstrate that a cold refresh can reconstruct the same output as
a clean full extraction.

Commit target: `feat: add deterministic cold incremental refresh`

### Phase 3 — warm indexer session and manual refresh barrier — in progress

Deliver a single worker-owned session that keeps the loaded solution, project
graph, Roslyn state where safe, contribution cache, dirty set, and generation
state in memory. A manual refresh request must:

- wait while the initial cold reconciliation is running;
- coalesce with an existing refresh instead of starting duplicate work;
- promote required work over background work;
- wait until the requested generation is indexed and serialized;
- validate and atomically publish `csharp.json`; and
- return only after publication succeeds.

Current progress: beginning the single-worker request coordinator. The one-shot
engine from Phase 2 is the cold/rebuild fallback; this phase adds in-memory
workspace reuse, coalesced requests, dirty generations, and a foreground
publication barrier without changing the public Graphify document.

Clean requests return the current published generation without loading Roslyn
or rewriting JSON. Events arriving after a request’s target generation remain
pending for the next refresh.

Gate: a controllable test session proves that a request arriving during a slow
cold load waits without duplicate extraction, warm clean requests are cheap,
multiple callers share one generation, and a failed refresh never publishes a
partial document.

Commit target: `feat: add warm incremental refresh session`

### Phase 4 — trusted file watcher and background indexing

Deliver the long-running `--watch` mode using the session from Phase 3. The
watcher subscribes before its initial cold reconciliation, records filesystem
events immediately, and maps them to dirty paths/projects. Event callbacks must
only normalize and enqueue paths on a bounded worker queue; they must never
load Roslyn or perform extraction. It may process dirty projects on a
low-priority background queue, but file events do not directly publish
Graphify JSON.

The watcher combines the .NET `FileSystemWatcher` fast path with a configurable
backup `PeriodicTimer` reconciliation (initial default: five minutes). The
backup path enumerates the configured input inventory and compares cheap
metadata, escalating to content verification or cold reconciliation when the
inventory is uncertain. Queue overflow, watcher `Error`/buffer overflow,
missing roots, failed scans, or an uncertain event boundary tear down and
recreate the watcher and invalidate the session; recovery scans current roots
and completes a cold reconciliation before readiness. No user files are
deleted during recovery.

The watcher is trusted only within its healthy session. A new or restarted
watcher creates a new session and performs cold reconciliation before becoming
ready. Watcher errors, event-buffer overflow, lost roots, or an uncertain
event boundary invalidate the session and force cold reconciliation. Event
capture has no correctness dependency on time-based debounce; background work
may coalesce project requests and a manual refresh bypasses any delay.

The normal foreground CLI invocation connects to a matching healthy watcher
through a local control channel. If no watcher is available, it performs the
cold reconciliation itself rather than silently using an unverified
incremental state. The watcher owns extraction, JSON serialization, and the
atomic output commit.

Gate: integration tests cover startup, restart, missed-event/error fallback,
manual refresh while cold or warm work is running, background-to-foreground
promotion, dirty work coalescing, event queue overflow, backup-timer detection,
watcher recreation, failed reconciliation, and output visibility during
replacement. The watcher must not create a second MSBuild workspace for a
foreground request.

Commit target: `feat: add trusted watcher and manual refresh protocol`

### Phase 5 — release hardening and performance validation

Deliver:

- the explicit cache-invalidating `--rebuild` path through both standalone and
  watcher refreshes;
- diagnostics/status for starting, ready, dirty, refreshing, and failed
  sessions;
- recovery after process termination and incomplete cache/output swaps;
- focused and end-to-end tests for the documented command flows; and
- performance measurements separating workspace load, Roslyn extraction,
  cache merge, metadata reconciliation, serialization, and foreground wait
  time.

The pinned real-world e2e must exercise a cold start, a warm no-change refresh,
a changed source refresh, watcher restart, and rebuild-from-scratch. Results
must remain deterministic and Graphify-compatible.

Gate: full .NET 10 and .NET 11 test suites, Release builds, package/tool smoke
tests, real-world determinism, watcher lifecycle tests, and `git diff --check`.
Stop optimization once the obvious workspace-reuse and work-coalescing gains
are demonstrated and further changes show diminishing returns.

Commit target: `test: harden incremental indexing and refresh lifecycle`

## Explicit non-goals for the initial release

- proving runtime reachability in the presence of reflection or arbitrary
  dependency injection;
- emitting separate graph nodes for unnamed syntax artifacts or compiler-
  generated implementation details;
- classifying callers as production/test/mixed or zero-reference;
- replacing Graphify’s generic syntax extractor for every language;
- requiring Rider or commercial analyzers in CI;
- treating an InspectCode warning or IDE result as the source of truth; and
- a 100% line or branch coverage target.

## Definition of done

The tool can be run headlessly from a clean checkout, produces stable Graphify
JSON for a real C# solution, project, or file-based app, identifies direct
callers by semantic symbol rather than method-name text, preserves namespace
and compiler-known entry-point facts for downstream queries, handles optional
versus ambiguous TFM selection explicitly, and has enough focused tests to make
changes to those contracts safe.

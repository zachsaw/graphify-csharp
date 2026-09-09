# Graphify C# implementation plan

## Goal

Ship a small, reusable, OSS Graphify C# semantic enricher. Given a C# solution
or project, it deterministically emits:

- source declaration nodes for namespaces, named types, type members, enum
  values, local functions, parameters, locals, type parameters, aliases,
  labels, and query range variables with stable, project/TFM-aware symbol
  identity;
- directed edges for Roslyn-resolved calls, inheritance, and non-call
  references;
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

Verification: .NET 10 (34 tests) and .NET 11 (37 tests) pass; the C# 15
fixture produces 89 nodes and 58 edges deterministically; the packed net10 and
net11 assets install and execute successfully; the pinned Dapper extraction
produces 3,656 nodes and 4,616 edges deterministically; and the full
`package-smoke` workflow passes under Docker/`act`.

Commit: `feat: cover CSharp15 semantic feature shapes`

## Explicit non-goals for v1

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
JSON for a real C# solution, identifies direct callers by semantic symbol rather
than method-name text, preserves namespace and compiler-known entry-point facts
for downstream queries, handles optional versus ambiguous TFM selection
explicitly, and has enough focused tests to make changes to those contracts
safe.

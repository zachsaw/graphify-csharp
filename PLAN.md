# Graphify C# implementation plan

## Goal

Ship a small, reusable, OSS Graphify enricher that can deterministically answer:

- which methods have zero statically resolved references;
- which methods are called only from namespaces that match the repository’s test
  naming convention;
- which methods have at least one production caller; and
- what evidence led to each result.

The first release is intentionally narrower than a whole-program runtime
reachability proof. Reflection, dependency injection, native/Wasm calls, and
other dynamic mechanisms must be represented as explicit uncertainty or
configured roots rather than guessed.

## Delivery principles

- Prefer Roslyn/MSBuild semantic information over text search or IDE-only output.
- Keep the graph model and policies independent from project loading so the
  important behavior is cheap to unit test.
- Preserve Graphify compatibility and add metadata only where it improves
  reproducibility or auditability.
- Make the common path useful quickly; defer expensive or speculative analysis
  until the direct semantic graph is stable.
- Every slice gets a focused verification gate and a commit before the next
  slice. High-value coverage is required; 100% coverage is not a release gate.

## Slices

### 1. Repository contract and release shape — complete

Deliver:

- this plan;
- the local `graphify-csharp` skill based on the the repository's C# and SOLID guidance;
- a minimal README describing the outcome and scope.

Gate: clean patch, valid skill frontmatter, and documented acceptance criteria.

Commit: `docs: define Graphify C# enricher plan and skill`

### 2. Pure domain model and canonical symbol identity — complete

Deliver deterministic records for symbols, relationships, provenance, source
locations, and caller classification. Canonical keys must distinguish projects,
TFMs, overloads, generic arity, nested types, and ref-like parameter shapes.

Gate: unit tests for identity collisions, edge deduplication, validation, and
test-namespace classification inputs without Roslyn or MSBuild.

Commit: `feat: add deterministic graph domain model`

### 3. Roslyn project loading and declaration catalog — complete

Deliver a headless loader for an explicit `.sln`, `.slnx`, or `.csproj` and a
catalog of source declarations. Record project and target-framework context;
do not silently merge symbols from different target frameworks.

Gate: fixture solution loads from the CLI/test harness and produces stable
declaration keys on the installed SDK.

Commit: `feat: load CSharp projects and catalog symbols`

### 4. Direct semantic references and callers — next

Deliver extraction of direct invocation/call, method-group/delegate, type,
attribute, `typeof`, and related Roslyn-resolved references. Orient caller edges
from source to target and retain source locations and relation provenance.

Gate: focused fixtures cover overload resolution, constructors, interfaces,
virtual methods, partial declarations, generics, and test namespaces. The
result must be deterministic across repeated runs.

Commit: `feat: extract deterministic CSharp semantic references`

### 5. Audit classification and safe uncertainty boundaries

Deliver zero-reference, production-used, test-only, and mixed classifications
using the configurable namespace naming convention. Add explicit handling for
entry points and dynamic/external roots so “no observed edge” is never silently
reported as “safe to delete.”

Gate: table-driven policy tests, including mixed callers, transitive reachability
policy, `nameof`/compile-time-only uses, and configured roots.

Commit: `feat: classify production and test-only callers`

### 6. Graphify serialization and command-line interface

Deliver Graphify-compatible JSON, a versioned metadata envelope where needed,
stable ordering, diagnostics, and a headless command that accepts the solution,
configuration/TFM, test namespace pattern, and output path.

Gate: golden JSON tests plus a subprocess smoke test against the fixture solution;
invalid input produces actionable diagnostics and a non-zero exit code.

Commit: `feat: emit Graphify CSharp enrichment output`

### 7. OSS release hardening

Deliver packaging, usage documentation, sample output, CI for supported SDKs,
and a small compatibility matrix. Keep optional dispatch expansion and advanced
reflection heuristics out of the critical release path unless the direct graph
reveals a concrete need.

Gate: clean checkout build/test, package install/run smoke test, and documented
known limitations.

Commit: `docs: prepare Graphify CSharp enricher release`

## Explicit non-goals for v1

- proving runtime reachability in the presence of reflection or arbitrary
  dependency injection;
- replacing Graphify’s generic syntax extractor for every language;
- requiring Rider or commercial analyzers in CI;
- treating an InspectCode warning as the source of truth; and
- a 100% line or branch coverage target.

## Definition of done

The tool can be run headlessly from a clean checkout, produces stable Graphify
JSON for a real C# solution, identifies direct callers by semantic symbol rather
than method-name text, separates test-only from production callers using the
configured namespace convention, reports zero observed references with the
appropriate uncertainty caveat, and has enough focused tests to make changes to
those contracts safe.

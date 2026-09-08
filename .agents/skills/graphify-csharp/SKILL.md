---
name: graphify-csharp
description: Build the Roslyn-backed C# semantic Graphify enricher that emits deterministic nodes, caller/reference edges, provenance, and compiler facts for downstream analysis.
---

# Graphify C# enricher

## Outcome

This repository builds a headless, reusable C# semantic enricher for Graphify.
Its output is the evidence layer, not the repository-specific analysis layer:

- source declaration nodes with stable semantic identity;
- directed `calls` and `references` edges resolved by Roslyn;
- namespace, project, TFM, source-location, and compiler-known entry-point facts;
  and
- deterministic Graphify-compatible JSON with diagnostics and provenance.

Graphify or another consumer can derive callers by following incoming edges and
can classify test-only, production, mixed, or zero-inbound-reference symbols
using its own query/policy. Do not add test-namespace or production-root policy
to this enricher.

## Working method

Read `PLAN.md` before changing architecture. Work in small vertical slices:

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

### Edges describe evidence

In v0.1, emit directed edges from source to target:

- `calls` for a Roslyn-resolved invocation or object creation;
- `references` for supported non-call symbol uses such as method groups,
  `typeof`, attributes, and type/member references;
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

## Verification

For every semantic change, test the smallest contract that changed and at least
one end-to-end fixture when the change crosses Roslyn or Graphify boundaries.
Prioritize:

- overloaded, generic, nested, partial, and multi-project symbol identity;
- caller-to-callee direction and overload resolution;
- namespace/project/TFM metadata and compiler-known entry-point facts;
- deterministic ordering and duplicate elimination;
- malformed input and unsupported-language diagnostics; and
- stable Graphify JSON consumed by the real CLI path.

Use the installed/pinned .NET SDK and explicit solution configuration, with an
optional TFM selector that becomes mandatory when target selection is
ambiguous. Do not rely on Rider for correctness or CI; Rider/InspectCode may be
used as an optional independent comparison during investigation.

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

---
name: graphify-csharp
description: Build and audit the Roslyn-backed C# semantic enricher that emits deterministic Graphify nodes, caller/reference edges, and production-versus-test usage classifications.
---

# Graphify C# enricher

## Outcome

This repository builds a headless, reusable C# semantic enricher for Graphify.
Its first useful outcome is an evidence-backed audit of method usage:

- `ProductionUsed`: at least one observed caller is outside the configured test
  namespace convention;
- `TestOnly`: observed callers exist and all are in test namespaces;
- `Mixed`: both test and non-test callers are observed; and
- `ZeroReferences`: no supported static reference to the symbol was observed.

`ZeroReferences` is an observation, not a deletion recommendation. Reflection,
dependency injection, native/Wasm entry points, generated code, and other
runtime mechanisms need explicit roots or an uncertainty record.

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
edge direction, classification, serialization, and representative Roslyn
language constructs.

## Design rules

### Keep actors small

Use separate components for project loading, symbol identity, declaration
cataloging, direct reference extraction, audit classification, and Graphify
serialization. A component should have one reason to change and a narrow input
and output contract. Avoid classes named `Manager`, `Helper`, or `Orchestrator`
that hide several policies. Pure records and pure functions are preferred where
they make behavior obvious.

Load Roslyn/MSBuild at the boundary. Keep domain classification and output
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

Use the full canonical key as audit evidence. If Graphify’s current node-ID
constraints require a compact ID, derive a stable ID from the key and retain the
full key in node properties. Never use a process-local hash, source line, or
unordered collection to define identity.

### Edges describe evidence

At minimum, emit directed edges from caller/source to target:

- `calls` for a Roslyn-resolved invocation or object creation;
- `references` for supported non-call symbol uses such as method groups,
  `typeof`, attributes, and type/member references;
- `implements` and `overrides` for declaration relationships.

Attach relation provenance and source location. Direct semantic facts are
`EXTRACTED`; conservative call-graph expansion is `INFERRED`; reflection or
other unresolved mechanisms are `AMBIGUOUS` or represented by an explicit
external root. Do not create an edge merely because two names look alike.

Deduplicate by `(source, target, relation, location/provenance policy)` and emit
nodes and edges in stable order. Preserve enough detail to explain why a method
was classified.

### Test classification is a policy

Classify a caller using the configured namespace naming convention, normally a
namespace segment such as `Tests`, plus any documented project/assembly rules.
Keep classification separate from Roslyn traversal so repositories can change
their convention without changing extraction. Expose the caller list and the
classification inputs in the result.

### Treat dynamic behavior honestly

Static absence is not proof of runtime absence. Distinguish at least:

- observed static references;
- compile-time-only references such as `nameof` when relevant;
- configured roots such as application entry points or reflection registrations;
- unresolved/dynamic evidence; and
- symbols outside the analyzed source boundary.

Do not pretend that a full call graph can resolve arbitrary reflection, DI,
function pointers, P/Invoke, or host/Wasm callbacks. Make those limits visible
in diagnostics and documentation.

## Verification

For every semantic change, test the smallest contract that changed and at least
one end-to-end fixture when the change crosses Roslyn or Graphify boundaries.
Prioritize:

- overloaded, generic, nested, partial, and multi-project symbol identity;
- caller-to-callee direction and overload resolution;
- test-only, production, mixed, and zero-observed-reference cases;
- deterministic ordering and duplicate elimination;
- malformed input and unsupported-language diagnostics; and
- stable Graphify JSON consumed by the real CLI path.

Use the installed/pinned .NET SDK and explicit solution configuration/TFM. Do
not rely on Rider for correctness or CI; Rider/InspectCode may be used as an
optional independent comparison during investigation.

Before each slice commit, run the narrow tests first, then the full test suite,
`dotnet build`, and `git diff --check`. Record known limitations instead of
weakening the semantic contract to make a test pass.

## Graphify integration

Follow Graphify’s extractor contract and current schema at the integration
boundary. Keep C# semantic metadata additive and versioned when it cannot fit
the base schema. Use directed mode for caller/callee analysis; an undirected
rendering loses the distinction required by this audit. Keep extraction
deterministic and avoid inventing relationships to satisfy a visualization.

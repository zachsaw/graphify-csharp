# Contributing to Graphify C#

These instructions apply to developing the Graphify C# extractor in this
repository. They are not instructions for projects that merely use the
installed tool.

The copyable consumer skill at
`.agents/skills/graphify-csharp/SKILL.md` must stay self-contained and focused
on using the CLI against the user's codebase. Keep extractor architecture,
implementation rules, repository test commands, and release procedures here
or in the contributor documentation. Do not make installing that skill impose
these development policies on another repository.

## Working method

Read the relevant design documents under `docs/` before changing architecture.
Work in small vertical slices:

1. define or preserve a narrow contract;
2. implement the smallest independently testable unit;
3. add high-value tests for the behavior and its failure boundary;
4. run formatting/build/tests and a relevant fixture smoke test;
5. complete and verify the slice before moving on; commit when requested.

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
limitations in the relevant docs instead of silently weakening the contract.

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

## Graphify format compatibility

Follow Graphify's extraction contract at the serialization boundary. Keep C#
metadata additive and versioned, preserve directed relationships, and keep
output deterministic. The complete raw JSON remains the source of semantic
evidence when a consumer's clustered graph normalizes parallel edges.

The CLI currently emits one complete document. If same-repository output
sharding is added later, it needs a deterministic merger that unions nodes by
stable ID, preserves parallel relations, and validates endpoints. Graphify's
merge workflow for independent sources prefixes IDs and is not that merger.

## Contributor commands

Run these from this repository when changing the extractor:

~~~bash
dotnet restore Graphify.CSharp.sln
dotnet build Graphify.CSharp.sln --configuration Release
dotnet test Graphify.CSharp.sln --configuration Release
dotnet pack src/Graphify.CSharp.Cli --configuration Release
~~~

Use `scripts/run-real-world-e2e.sh` for the pinned real-world package path and
`scripts/run-watcher-e2e.sh` for packaged watcher behavior. See
`docs/RELEASING.md` for release gates and `docs/INCREMENTAL_INDEXING.md`
before changing watcher concurrency or recovery.

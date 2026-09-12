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

Run the ordinary command without `--watch` whenever fresh evidence is needed.
It waits through matching-watcher startup, indexing, and recovery and returns
after the requested complete JSON is current. File events alone do not
republish the public JSON. Without a matching live watcher, the ordinary
command performs a one-shot refresh if the destination is available.

A different analysis configuration targeting an active watcher's output
fails with an ownership conflict. Use another output or stop the relevant
watcher when changing configuration. A different output is refreshed
independently; it is never redirected to the existing watcher's output.

Add `--rebuild` to the ordinary command to invalidate cached contributions.
Stop the watcher before manually removing its `.graphify-csharp/` directory,
which also holds live leases. A watcher restart performs cold reconciliation;
it cannot resume a previously trusted event stream.

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
the C# document as above before a C# query, path, explanation, or export, and
pass that document explicitly:

~~~bash
graphify query "Which methods call the order service?" \
  --graph ./graphify-out/csharp.json
~~~

Graphify's general skill teaches its graph workflows; this skill supplies the
C# refresh and evidence-reading steps alongside it. Preserve raw
`csharp.json` for audits: a clustered view can normalize parallel
relationships, and name-based graph nodes cannot be joined to semantic IDs
merely by matching labels.

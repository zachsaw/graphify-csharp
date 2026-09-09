# Usage

## Install

The release artifact is a .NET global tool:

```text
dotnet tool install --global Graphify.CSharp
```

To build and run the current checkout instead:

```text
dotnet run --project src/Graphify.CSharp.Cli -- --help
dotnet pack src/Graphify.CSharp.Cli --configuration Release
```

## Extract a repository

Use a repository-relative root so symbol keys and source files do not depend on
the machine’s absolute path:

```text
graphify-csharp \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
```

The input may be a solution, solution filter supported by MSBuild, or project
file. A project input also loads its project references that MSBuildWorkspace
reports. The TFM selector is optional for single-target projects. For a
multi-target project, specify one TFM; the tool refuses to silently merge
different compilations.

To check repeatability, run the local CLI twice and compare the complete output:

```text
./scripts/check-deterministic-extraction.sh \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release
```

## Read the graph

Nodes contain stable C# properties:

- `symbol_key`: the complete project/TFM-aware semantic identity;
- `namespace`: the containing namespace, or an empty string for the global
  namespace;
- `project`: repository-relative project path; and
- `target_framework`: the compilation’s selected TFM; and
- `declaration_kind`: the Roslyn declaration shape, such as `class`, `enum`,
  `enum_member`, `method`, `localfunction`, `parameter`, `local`, `alias`,
  `label`, `range_variable`, or `indexer`.

Edges point from the source declaration to the referenced declaration. Reverse
the edges in Graphify to obtain callers. `calls`, `references`, `implements`,
`inherits`, and `overrides` are direct Roslyn evidence; locations on each edge
explain where the relationship was observed. Compiler-known entry points are
marked on their node as `is_entry_point=true`.

The `graphify_csharp.diagnostics` array reports workspace-load issues and
recoverable declaration-identity issues. An unsupported or otherwise
unrepresentable Roslyn declaration is skipped with its kind, display name, and
repository-relative source location; other declarations continue to be
emitted.

The declaration catalog covers source namespaces, named types, constructors,
methods/operators/local functions, properties/indexers, fields/enum values,
events, parameters, locals, type parameters, aliases, labels, and query range
variables. Unnamed syntax artifacts and compiler-generated implementation
details are not separate graph nodes in v0.1.

This output is evidence for downstream analysis. The enricher deliberately does
not decide whether a caller is a test, whether a target has zero inbound edges,
or whether code is safe to delete.

## Graphify integration

The file is valid Graphify extraction JSON: it has the base `nodes`, `edges`, and
`hyperedges` arrays, required `file_type`/`source_file` node fields, and the
required edge confidence fields. It also marks itself as directed and
multigraph so Graphify’s raw JSON loader preserves edge direction and parallel
relationships. Generate the file before each Graphify query or export:

```text
graphify-csharp \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json

graphify query "Which methods call the service?" \
  --graph ./graphify-out/csharp.json
```

The semantic node IDs are stable hashes of full C# symbol keys, so this output
is intended to be the authoritative C# semantic extraction for the selected
scope; merging it with a name-only C# extraction requires an explicit ID-join
layer. Graphify’s clustered NetworkX view can normalize multiple relationships
between the same endpoints; retain `csharp.json` when relation-level evidence
matters.

## Known limitations

The extractor follows Roslyn-resolved source symbols. It does not claim to
resolve arbitrary reflection strings, DI registrations, function pointers,
P/Invoke, generated code excluded by the project, or host callbacks. Add
consumer-specific roots and policies in downstream analysis, and review static
limitations before acting on zero-inbound-reference results.

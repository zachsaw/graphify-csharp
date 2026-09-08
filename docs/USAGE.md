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

## Read the graph

Nodes contain stable C# properties:

- `symbol_key`: the complete project/TFM-aware semantic identity;
- `namespace`: the containing namespace, or an empty string for the global
  namespace;
- `project`: repository-relative project path; and
- `target_framework`: the compilation’s selected TFM.

Edges point from the source declaration to the referenced declaration. Reverse
the edges in Graphify to obtain callers. `calls`, `references`, `implements`,
and `overrides` are direct Roslyn evidence; locations on each edge explain where
the relationship was observed. Compiler-known entry points are marked on their
node as `is_entry_point=true`.

This output is evidence for downstream analysis. The enricher deliberately does
not decide whether a caller is a test, whether a target has zero inbound edges,
or whether code is safe to delete.

## Graphify integration

The file is valid Graphify extraction JSON: it has the base `nodes`, `edges`, and
`hyperedges` arrays, required `file_type`/`source_file` node fields, and the
required edge confidence fields. Use Graphify’s directed mode for caller/callee
questions. The semantic node IDs are stable hashes of full C# symbol keys, so
this output is intended to be the authoritative C# semantic extraction for the
selected scope; merging it with a name-only C# extraction requires an explicit
ID-join layer.

## Known limitations

The extractor follows Roslyn-resolved source symbols. It does not claim to
resolve arbitrary reflection strings, DI registrations, function pointers,
P/Invoke, generated code excluded by the project, or host/Wasm callbacks. Add
consumer-specific roots and policies in downstream analysis, and review static
limitations before acting on zero-inbound-reference results.

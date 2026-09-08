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

## Analyze a repository

Use a repository-relative root so symbol keys and source files do not depend on
the machine’s absolute path:

```text
graphify-csharp \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --target-framework net10.0 \
  --test-namespace Tests \
  --output ./graphify-out/csharp.json
```

The input may be a solution, solution filter supported by MSBuild, or project
file. A project input also loads its project references that MSBuildWorkspace
reports. Specify one TFM when a project is multi-targeted; do not merge output
from different TFMs unless that is an intentional analysis decision.

## Read the audit

Each entry in `graphify_csharp.audit` has a target node ID and one of:

- `production_used`: at least one caller is outside the configured test
  namespace convention;
- `test_only`: callers exist and every observed caller is in a test namespace;
- `mixed`: both kinds of caller exist; or
- `zero_references`: no supported static inbound edge was observed.

The `callers` array is the evidence list. It includes caller node ID, display
label, namespace, classification, relation names, provenance, and source
locations. `warnings` makes uncertainty visible. A zero-reference result is an
audit observation, not permission to delete code.

## Graphify integration

The file is valid Graphify extraction JSON: it has the base `nodes`, `edges`, and
`hyperedges` arrays, required `file_type`/`source_file` node fields, and the
required edge confidence fields. Use Graphify’s directed mode for caller/callee
questions. The semantic node IDs are stable hashes of full C# symbol keys, so
this output is intended to be the authoritative C# semantic extraction for the
analyzed scope; merging it with a name-only C# extraction requires an explicit
ID-join layer.

## Known limitations

The analyzer follows Roslyn-resolved source symbols. It does not claim to
resolve arbitrary reflection strings, DI registrations, function pointers,
P/Invoke, generated code excluded by the project, or host/Wasm callbacks. Add a
known production root with `--production-root` and review all warnings before
acting on zero-reference results.

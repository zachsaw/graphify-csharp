# Graphify C#

A headless Roslyn/MSBuild semantic enricher for
[Graphify](https://github.com/Graphify-Labs/graphify).

It emits deterministic C# declaration nodes, directed Roslyn-resolved semantic
relationships, provenance, and stable source locations in Graphify’s JSON shape.
Downstream Graphify queries can use the edges and namespace metadata to answer
repository-specific questions such as caller and zero-inbound-reference audits.

## Quick start

Run from a repository containing the solution or project you want to inspect:

```text
dotnet tool install --global Graphify.CSharp
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
```

The package is currently built from this repository as version `0.1.0` while
the API and Graphify integration settle. For local development, replace the
install command with:

```text
dotnet run --project src/Graphify.CSharp.Cli -- --input ./src/MyProduct.sln --root .
```

`--target-framework` is optional. The loader resolves a single project target
automatically; pass it when a project targets multiple frameworks. An ambiguous
multi-target project fails with an actionable message instead of producing a
mixed graph.

## Output

The output keeps Graphify’s required `nodes`, `edges`, and `hyperedges` arrays.
Edges are directed from source/caller to target/contract and use `EXTRACTED` for
Roslyn-resolved facts. v0.1 emits `calls`, `references`, `implements`, and
`overrides`; node properties include the full symbol key, namespace, project,
and target framework. `graphify_csharp` contains only the versioned extractor
metadata and loader diagnostics.

The enricher does not classify callers or decide whether a declaration is safe
to remove. Reflection, dependency injection, generated code, native callbacks,
and other runtime mechanisms are outside static extraction and must be handled
by the consuming analysis.

## Scope of v0.1

Included:

- `.sln`, `.slnx`, and `.csproj` loading through MSBuildWorkspace;
- overload-aware symbol identity including project and TFM context;
- direct calls, constructors, method groups, properties, fields, events, and
  `typeof` references;
- interface implementation and virtual override relationships;
- stable Graphify JSON and a dependency-free command-line parser.

Not a runtime reachability proof. Interface/virtual dispatch expansion,
reflection heuristics, DI container modeling, and host callbacks are deliberately
bounded in v0.1 and will be added only with explicit provenance and fixtures.

## Development

```text
dotnet test Graphify.CSharp.sln --configuration Release
dotnet build Graphify.CSharp.sln --configuration Release
dotnet pack src/Graphify.CSharp.Cli --configuration Release
```

To verify byte-for-byte repeatability against a fixture or another solution:

```text
./scripts/check-deterministic-extraction.sh \
  --input ./src/MyProduct/MyProduct.sln \
  --root . \
  --configuration Release
```

The implementation slices and acceptance gates are in [PLAN.md](PLAN.md). The
repository’s reusable development contract is in
[.agents/skills/graphify-csharp/SKILL.md](.agents/skills/graphify-csharp/SKILL.md).
See [docs/USAGE.md](docs/USAGE.md) for output details and
[docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) for the supported v0.1 path.

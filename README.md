# Graphify C#

A headless Roslyn/MSBuild semantic enricher for
[Graphify](https://github.com/Graphify-Labs/graphify).

It answers a focused audit question: which C# declarations have production
callers, only test callers, both, or zero observed static references? It emits
deterministic symbol nodes, directed semantic relationships, caller evidence,
and stable source locations in Graphify’s JSON shape.

## Quick start

Run from a repository containing the solution or project you want to inspect:

```text
dotnet tool install --global Graphify.CSharp
graphify-csharp \
  --input ./src/MyProduct.sln \
  --root . \
  --configuration Release \
  --target-framework net10.0 \
  --test-namespace Tests \
  --output ./graphify-out/csharp.json
```

The package is currently built from this repository as version `0.1.0` while
the API and Graphify integration settle. For local development, replace the
install command with:

```text
dotnet run --project src/Graphify.CSharp.Cli -- --input ./src/MyProduct.sln --root .
```

Pass an explicit `--target-framework` for multi-targeted projects. The default
test convention treats any namespace segment named `Tests` as a test caller;
change it with `--test-namespace`.

## Output

The output keeps Graphify’s required `nodes`, `edges`, and `hyperedges` arrays.
Edges are directed from caller/source to callee/target and use `EXTRACTED` for
Roslyn-resolved facts. C#-specific audit evidence is additive under
`graphify_csharp.audit`, including each direct caller, its namespace-based
classification, relation, source locations, and warnings.

`ZeroReferences` means “no supported static reference was observed.” It is not
a safe-delete proof: reflection, dependency injection, generated code,
native/Wasm callbacks, and other runtime entry points require explicit roots or
human review. Use `--production-root <node-id>` for a known production root.

## Scope of v0.1

Included:

- `.sln`, `.slnx`, and `.csproj` loading through MSBuildWorkspace;
- overload-aware symbol identity including project and TFM context;
- direct calls, constructors, method groups, properties, fields, events, and
  `typeof` references;
- production/test/mixed/zero-observed classification by namespace convention;
- stable Graphify JSON and a dependency-free command-line parser.

Not a runtime reachability proof. Virtual/interface dispatch expansion,
reflection heuristics, DI container modeling, and host callbacks are deliberately
bounded in v0.1 and will be added only with explicit provenance and fixtures.

## Development

```text
dotnet test Graphify.CSharp.sln --configuration Release
dotnet build Graphify.CSharp.sln --configuration Release
dotnet pack src/Graphify.CSharp.Cli --configuration Release
```

The implementation slices and acceptance gates are in [PLAN.md](PLAN.md). The
repository’s reusable development contract is in
[.agents/skills/graphify-csharp/SKILL.md](.agents/skills/graphify-csharp/SKILL.md).
See [docs/USAGE.md](docs/USAGE.md) for output details and
[docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) for the supported v0.1 path.

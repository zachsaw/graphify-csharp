# Compatibility

## Tool runtime

The v0.1 tool targets .NET 10 and uses Roslyn 5.9 with MSBuildWorkspace. CI
builds on the current .NET 10 SDK line. The tool is headless and does not
require Rider or InspectCode.

## Input projects

MSBuild must be able to evaluate the supplied `.sln`, `.slnx`, or `.csproj` on
the host. Install any SDKs/workloads required by the analyzed repository. For a
single-target project, `--target-framework` may be omitted and the loader
records the evaluated target automatically. For a multi-targeted project, pass
one value so the symbol key cannot silently combine different compilations.

## Graphify output

The emitted document follows Graphify’s v8 extraction shape and adds a
`graphify_csharp` extractor-metadata block. The C# semantic node IDs are
lowercase stable SHA-256-derived IDs; the full project/TFM-aware symbol key
remains in node properties. This avoids overload collisions, but means a
name-only C# extraction must use an explicit ID join before merging.

## Validation matrix

The repository currently validates the following path in CI and local tests:

| Component | Validated value |
| --- | --- |
| .NET SDK | 10.0.x |
| Roslyn/MSBuild packages | 5.9.0 |
| Input | C# `.csproj` fixture; loader also accepts `.sln`/`.slnx` extensions |
| Output | Graphify nodes, edges, hyperedges, and C# extractor metadata |
| Test runner | xUnit on .NET 10 |

Older SDK support and multi-target matrix builds can be added once a real
consumer requires them; they are not silently claimed by v0.1.

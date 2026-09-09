# Compatibility

## Tool runtime

The v0.1 tool targets .NET 10 and uses Roslyn 5.9 with MSBuildWorkspace. CI
builds on the current .NET 10 SDK line. The tool is headless and does not
require Rider or InspectCode.

The declaration and identity paths are designed around Roslyn symbol
interfaces rather than syntax-name assumptions. This includes C# 14 extension
blocks (including receiver parameters and extension indexer-shaped properties
when the compiler exposes them), partial members, and compiler-generated
containing scopes. A future language form that Roslyn exposes but this version
cannot identify is reported as a recoverable Graphify diagnostic.

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
| C# language features | C# 14 fixture coverage; extension blocks, field-backed properties, partial constructors/events, explicit compound-assignment operators, span/lambda/assignment forms |
| Roslyn/MSBuild packages | 5.9.0 |
| Input | C# `.csproj` fixture; loader also accepts `.sln`/`.slnx` extensions |
| Output | Graphify nodes, edges, hyperedges, and C# extractor metadata |
| Test runner | xUnit on .NET 10 |

Older SDK support and multi-target matrix builds can be added once a real
consumer requires them; they are not silently claimed by v0.1.

C# 15 requires a .NET 11 preview (or newer) compiler/toolchain. The current
repository validation environment has only .NET 10 SDKs, so C# 15 syntax is
not included in the local test claim until that Roslyn/MSBuild matrix is added.

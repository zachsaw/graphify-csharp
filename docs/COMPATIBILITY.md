# Compatibility

## Tool runtime

The v0.1 package contains two .NET global-tool assets:

| Tool asset | Runtime required to run it | Intended compiler surface |
| --- | --- | --- |
| `net10.0` | .NET 10 runtime | Roslyn 5.9 / C# 14 |
| `net11.0` | .NET 11 runtime | .NET 11 SDK Roslyn / C# 15 preview |

Install or update the same package with `dotnet tool ... --framework` to select
the asset. The tool is headless and does not require Rider or InspectCode. The
`net11.0` build currently takes its C# 15-capable Roslyn workspace assemblies
from the matching .NET 11 SDK during packaging, so maintainers need that SDK
installed to produce the complete multi-target package.

The declaration and identity paths are designed around Roslyn symbol
interfaces rather than syntax-name assumptions. This includes C# 14 extension
blocks (including receiver parameters), partial members, compiler-generated
containing scopes, and the C# 15 declaration shapes exposed by the .NET 11
Roslyn surface. A future language form that Roslyn exposes but this version
cannot identify is reported as a recoverable Graphify diagnostic.

## Input

MSBuild must be able to evaluate the supplied `.sln`, `.slnx`, or `.csproj` on
the host. An SDK file-based `.cs` app is converted by the installed SDK before
Roslyn loads it; its SDK directives and referenced projects/packages must be
available locally. Install any SDKs/workloads required by the analyzed
repository. For a single-target project, `--target-framework` may be omitted
and the loader records the evaluated target automatically. For a multi-targeted
project, pass one value so the symbol key cannot silently combine different
compilations.

## Graphify output

The emitted document follows Graphify’s v8 extraction shape and adds a
`graphify_csharp` extractor-metadata block. The C# semantic node IDs are
lowercase stable SHA-256-derived IDs; the full project/TFM-aware symbol key
remains in node properties. This avoids overload collisions, but means a
name-only C# extraction must use an explicit ID join before merging. The CLI
currently emits one complete document. Any future shards must retain the same
envelope and stable IDs. Graphify’s current `merge-graphs` command prefixes
each input as an independent graph source, so same-repository shards require a
dedicated deterministic merger to union nodes by ID, preserve parallel
relations, validate endpoints, and emit one complete document.

## Validation matrix

The repository currently validates the following path in CI and local tests:

| Component | Validated value |
| --- | --- |
| .NET SDK / tool asset | 10.0.x / `net10.0` |
| C# language features | C# 14 fixture coverage; extension blocks, field-backed properties, partial constructors/events, explicit compound-assignment operators, span/lambda/assignment forms |
| Roslyn/MSBuild packages | 5.9.0 |
| Input | C# `.csproj` and SDK file-based `.cs` fixtures; loader also accepts `.sln`/`.slnx` |
| Output | Graphify nodes, edges, hyperedges, and C# extractor metadata |
| Test runner | xUnit on .NET 10 |

The same matrix also validates the .NET 11/`net11.0` asset with the C# 15
fixture and xUnit on .NET 11. The package smoke test installs both assets from
the same `.nupkg` and executes each one independently. Older SDK support is not
silently claimed by v0.1.

## C# 15 preview coverage

The C# 15 fixture exercises every feature listed in the public .NET 11 preview
documentation. The graph treatment is deliberately limited to declarations,
semantic references, and compiler facts:

| Feature | Graph treatment |
| --- | --- |
| Collection-expression arguments | Emits the compiler-selected constructor or collection-builder `calls` edge and traverses argument expressions. |
| Union types | Emits `union` nodes and references to generic, type-parameter, and ordinary case types; union bodies and patterns use the normal member/reference paths. |
| Closed hierarchies | Retains `is_closed=true`, normal inheritance edges, and references from exhaustive patterns to descendant types. |
| Extension indexers | Catalogs the extension indexer as a property with `declaration_kind=indexer` and resolves indexed access to it. |
| Labeled `break`/`continue` | Catalogs labels and emits references from labeled branches, including Roslyn’s internal branch-label representation. |
| Memory safety | Traverses pointer declarations, fixed statements, `sizeof`, `unsafe(...)`, and `safe` declarations without inferring runtime safety or caller obligations. |

The C# 15 memory-safety rules are still preview behavior. The enricher records
the declarations and references that Roslyn exposes; it does not attempt to
reimplement compiler safety enforcement.

C# 15 input requires the `net11.0` tool asset and a .NET 11 preview (or newer)
compiler/toolchain. If an input project is multi-targeted, pass its selected
compilation with `--target-framework`; this is independent of the tool asset
selection.

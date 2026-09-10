# Changelog

## 0.1.5

- Multi-target the global tool for .NET 10 and .NET 11, with C# 15
  collection-expression argument calls, union/case relationships, closed
  hierarchy facts, extension indexers, labeled branch targets, and
  memory-safety syntax in the `net11.0` asset.
- Support C# 14 extension-block receiver parameters and members with stable
  identities and correct caller attribution.
- Coalesce partial constructors, properties, and events while retaining all
  source locations.
- Preserve the remaining graph and emit diagnostics when a declaration shape
  cannot be assigned a stable identity.
- Add warm incremental indexing with resilient file-watcher recovery and
  foreground refresh barriers.
- Parallelize coarse semantic extraction while preserving deterministic output.
- Route watcher refreshes by both analysis configuration and output path so a
  request cannot be reported as writing another file.

## 0.1.0

- Added deterministic project/TFM-aware C# symbol identity.
- Added Roslyn/MSBuild declaration cataloging and direct semantic references.
- Added interface implementation and virtual override relationships.
- Added Graphify-compatible JSON and the `graphify-csharp` .NET tool command.
- Documented the extraction boundary and dynamic-analysis limitations.

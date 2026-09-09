# Changelog

## Unreleased

- Support C# 14 extension-block receiver parameters and members with stable
  identities and correct caller attribution.
- Coalesce partial constructors, properties, and events while retaining all
  source locations.
- Preserve the remaining graph and emit diagnostics when a declaration shape
  cannot be assigned a stable identity.

## 0.1.0

- Added deterministic project/TFM-aware C# symbol identity.
- Added Roslyn/MSBuild declaration cataloging and direct semantic references.
- Added interface implementation and virtual override relationships.
- Added Graphify-compatible JSON and the `graphify-csharp` .NET tool command.
- Documented the extraction boundary and dynamic-analysis limitations.

# Graphify C#

A Roslyn-backed semantic enricher for [Graphify](https://github.com/Graphify-Labs/graphify).

The first release is aimed at one practical question: which C# methods have no
production callers, only test callers, or a mixture of both? It will emit
deterministic symbol nodes and directed semantic relationships that Graphify can
consume, while preserving enough provenance to distinguish facts from
inferences.

The implementation plan is in [PLAN.md](PLAN.md). The repository’s development
contract is in [.agents/skills/graphify-csharp/SKILL.md](.agents/skills/graphify-csharp/SKILL.md).

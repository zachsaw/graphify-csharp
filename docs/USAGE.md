# Usage

`graphify-csharp` has two independent workflows:

| Need | Command | Result |
| --- | --- | --- |
| Create a complete file | `export` or bare invocation | One Graphify-compatible JSON document on disk |
| Keep a Roslyn workspace warm | `watch` | A foreground session; no JSON output |
| Ask one semantic question | `query` | A bounded JSON or human-readable response |
| Refresh trusted evidence | `refresh --instance ID` | A small acknowledgement; no JSON output |
| Inspect or control sessions | `ps`, `info`, `inspect`, `diagnostics`, `stop` | Management responses |

`graphify-csharp` is usable without the separate `graphify` executable. The
optional Graphify workflow is documented at the end of this page.

## Install

The release artifact is a .NET global tool. Choose the runtime asset that
matches the C# language surface you need:

```text
dotnet tool install --global Graphify.CSharp --framework net10.0
```

For C# 15 preview input, use the `net11.0` asset and install the corresponding
.NET 11 SDK:

```text
dotnet tool install --global Graphify.CSharp --framework net11.0
```

If the tool is already installed, use `dotnet tool update` with the same
`--framework` instead.

The install-time `--framework` selects the executable asset. It is unrelated
to the optional analysis-time `--target-framework`, which selects one target
framework from a multi-targeted project.

To run the current checkout while developing the tool:

```text
dotnet run --project src/Graphify.CSharp.Cli --framework net10.0 -- --help
dotnet pack src/Graphify.CSharp.Cli --configuration Release
```

If you are upgrading from v0.1, read the [v0.2 migration guide](MIGRATING_TO_V0.2.md)
before changing automation. It covers the explicit `watch`, `query`, `refresh`,
and `export --instance` routes, the new default output name, and caller-relative
path resolution.

## Agent setup

The [consumer skill](../.agents/skills/graphify-csharp/SKILL.md) teaches an
agent how to use the installed executable. It does not install the executable,
and it contains no maintainer instructions.

Project-local Codex setup:

```text
mkdir -p .agents/skills/graphify-csharp
curl -fsSL \
  https://raw.githubusercontent.com/zachsaw/graphify-csharp/main/.agents/skills/graphify-csharp/SKILL.md \
  -o .agents/skills/graphify-csharp/SKILL.md
```

Use `~/.codex/skills/graphify-csharp/SKILL.md` for a personal Codex install.
Use `.claude/skills/graphify-csharp/SKILL.md` in a project or
`~/.claude/skills/graphify-csharp/SKILL.md` for a personal Claude Code install.
Reload the agent after installing or updating the skill.

If the separate `graphify` tool is also part of the workflow, install its
general skill alongside this one. Their responsibilities are different:

- the `graphify-csharp` skill teaches this CLI's C# extraction, semantic
  queries, sessions, and export;
- the `graphify` skill teaches the separate `graphify` executable's graph
  queries, paths, explanations, clustering, and exports.

Neither skill installs or silently invokes the other tool.

## Paths and input discovery

Capture the caller's current directory once. Explicit relative `--input`,
`--root`, `--output`, `--path`, and `--project` values are resolved against
that directory. `--root` controls analysis scope and omitted-input discovery;
it does not rebase other explicitly supplied paths.

When `--input` is omitted for disk export or `watch`, the tool examines only
the immediate `--root` directory (or the caller's current directory):

1. one `.sln` or `.slnx` is selected;
2. multiple solutions, including a `.sln`/`.slnx` pair, are an error;
3. if there is no solution, one `.csproj` is selected;
4. multiple projects or no candidates are an error.

Discovery is case-insensitive and does not recurse. A lone `.cs` file is not
auto-selected, but an explicitly supplied file-based `.cs` app is supported.
An explicit input always wins over discovery, while supplying both a positional
input and `--input` is an error.

Other defaults are:

- `--root`: caller's current directory;
- `--configuration`: `Debug`;
- `--target-framework`: automatic selection when unambiguous;
- export output: `./graphify-out/csharp.json` under the caller's current
  directory; and
- watcher backup scan: five minutes.

The output default is deliberately caller-relative. For example, running from
`/work/tools` with `--root /work/product` writes
`/work/tools/graphify-out/csharp.json`, not inside `/work/product`.

## Export a complete document

Use the explicit verb when a complete JSON document is wanted:

```text
graphify-csharp export \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --output ./graphify-out/csharp.json
```

The following are equivalent disk routes:

```text
graphify-csharp
graphify-csharp export
graphify-csharp Product.sln
```

They use the same discovery, defaults, cache, and output transaction. `--json`
changes the command response to one structured envelope on stdout; progress
remains on stderr. `--rebuild` bypasses reusable project contributions:

```text
graphify-csharp export --input ./src/Product.sln --rebuild --json
```

If Roslyn or MSBuild cannot load the supplied `.sln` or `.slnx`, the command
returns a nonzero exit code. With `--json`, the response uses the stable error
code `solution_load_failed` and includes the input path plus the underlying
loader message. Treat this as an input or environment failure, not as an empty
semantic graph.

The complete output contains `nodes`, `edges`, and `hyperedges`. It is written
atomically and is the only public JSON publication performed by this CLI.

## Query semantic evidence

A cold query answers one question without a persistent worker or graph file:

```text
graphify-csharp query symbols Submit \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --kind method \
  --json
```

Cold queries require explicit `--input`; they do not use discovery. They do not
support cursors or snapshots. A selected `--instance` never falls back to cold
analysis when that session is missing or incompatible.

For repeated questions, use a warm session as described below, then route every
query explicitly:

```text
graphify-csharp query symbols Submit --instance <session-id> --kind method --json
graphify-csharp query signature --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query callers --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query usages --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query hierarchy --instance <session-id> --symbol <symbol-id> --direction implementations --json
graphify-csharp query arguments --instance <session-id> --symbol <symbol-id> --json
graphify-csharp query usage-summary --instance <session-id> --kind method --group-by project,namespace --json
```

`symbols` and `usage-summary` accept an optional case-insensitive substring.
The other verbs require one exact symbol ID, so overloaded and generic members
are not selected by spelling alone. `usage-summary` returns fixed inbound
counts and origin groups; it does not decide whether a declaration is dead or
test-only. Apply the repository's project/namespace convention to the returned
caller provenance.

The evidence includes project, namespace, target framework, source locations,
diagnostics, and a snapshot/scope. `calls`, `references`, `inherits`,
`implements`, and `overrides` are separate relationship kinds. Argument results
include the source call site and the compiler-bound formal parameter where it
can be represented.

Live responses are bounded pages. Continue with `page.next_cursor` while
keeping the same query, filters, symbol, and direction. A changed evidence
revision or recovery invalidates cursors and snapshots with a structured
`stale_snapshot` response.

## Keep a warm session

Start `watch` in a foreground terminal. It never takes an output path and never
writes a graph file:

```text
graphify-csharp watch
```

For an input under `src` or an ambiguous repository, specify it explicitly:

```text
graphify-csharp watch \
  --input ./src/Product/Product.sln \
  --root . \
  --configuration Release \
  --watch-scan-interval 00:05:00
```

The watcher prints a full session ID and resolved input/root/configuration/TFM
context on stderr while startup is in progress, followed by a ready message
and concrete query/export examples. Use another terminal for operations:

```text
graphify-csharp ps --json
graphify-csharp info <session-id-or-unique-prefix> --json
graphify-csharp query callers --instance <session-id> --symbol <symbol-id> --json
```

Every session is independent. No command selects a session by matching input,
root, configuration, or output, and starting a second watcher does not attach
to the first. Use the full ID or a prefix that matches exactly one session.

File events and backup inventory scans update trusted in-memory evidence when
possible, but they do not publish JSON. Queries, explicit refreshes, and live
exports wait through startup and recovery barriers:

```text
graphify-csharp refresh --instance <session-id>
graphify-csharp refresh --instance <session-id> --rebuild
graphify-csharp export --instance <session-id>
```

`refresh` returns a small acknowledgement and no graph file. `--rebuild`
invalidates reusable contributions for that refresh. `export --instance`
writes the current session to the caller's default output, or to a path given
with `--output`. Export is the explicit JSON publication boundary.

The watcher uses the evaluated MSBuild/Roslyn input set, not `.gitignore` or a
hard-coded directory-name blacklist. Evaluated source/additional documents,
project files, imports, restore metadata, linked files, and relevant generated
inputs are covered. Output/intermediate roots and exact tool-owned paths are
pruned only when evaluated project policy proves them irrelevant. A project
membership or dependency change triggers a conservative reload so MSBuild
remains authoritative.

Native file-watcher errors, queue overflow, missing roots, or an incomplete
backup scan invalidate the session and trigger cold recovery. A restarted
watcher always creates a new session and re-establishes a trusted boundary.
`--no-progress` suppresses presentation only; it does not change indexing or
routing.

## Inspect, diagnose, and stop sessions

Management does not load a project or Roslyn. It reads bounded per-user
descriptors and probes a selected local endpoint:

```text
graphify-csharp ps
graphify-csharp ps --json
graphify-csharp info <session-id-or-unique-prefix> --json
graphify-csharp inspect <session-id-or-unique-prefix> --json
graphify-csharp diagnostics <session-id-or-unique-prefix> \
  --output ./graphify-out/graphify-csharp-diagnostics.json
graphify-csharp stop <session-id-or-unique-prefix> --json
```

`info` and `inspect` are aliases. They report lifecycle state, readiness,
input identity, active stage, evidence counts, last operation, recovery state,
and cached resource samples. `ps` is intentionally smaller. Management reads
do not build the lazy semantic index or take a fresh blocking resource sample.

The registry is a discovery hint. A crashed process can leave a stale record;
stale records are reported, not used to kill a PID. `stop` requests graceful
shutdown and succeeds only after the selected process releases its resources.
There is no PID/name-based selection and no `stop --all`.

`diagnostics` asks a live session for a bounded report, then the requesting CLI
writes it locally. The watcher never writes to that destination, and the
destination must not already exist. Reports contain paths, runtime identity,
stage/timing history, evidence counts, recovery history, and process metrics;
they do not contain source contents or a heap dump and are not anonymized.

## Graphify integration

Graphify is optional. `graphify-csharp` does not invoke or require the separate
`graphify` executable. Without it, consume query responses or the complete JSON
with an agent, `jq`, C#, Python, or another program.

When both tools are installed, run two explicit stages:

```text
# This tool: compiler-bound C# extraction
graphify-csharp export \
  --input ./src/Product/Product.sln \
  --root . \
  --output ./graphify-out/csharp.json

# The separate Graphify tool: higher-level graph workflow
graphify query "Which methods call the service?" \
  --graph ./graphify-out/csharp.json
```

The `graphify-csharp` skill teaches the first stage. Graphify's general skill
teaches the second. Do not confuse `graphify-csharp query` with `graphify query`:
the former asks this tool for semantic C# evidence; the latter reads a graph
document using the separate tool.

## Static-analysis boundary

The output describes what Roslyn can observe in the selected compilation. It is
not runtime reachability. Reflection, dependency injection, dynamic invocation,
native callbacks, external consumers, and generated code excluded from the
evaluated project can create relationships absent from the graph.

In particular:

- zero inbound edges means zero observed static references in the selected
  scope;
- a test-only classification depends on the project/namespace convention the
  consumer applies; and
- deletion candidates still need review of entry points, reflection, DI,
  source generators, public API consumers, and build/test behavior.

Unsupported or unrepresentable semantic shapes are surfaced as diagnostics
where possible instead of crashing the complete extraction.

## Qualification and development

The repository includes repeatable package-installed E2E checks:

```text
./scripts/run-query-e2e.sh
./scripts/run-watcher-e2e.sh
```

For source development:

```text
dotnet restore Graphify.CSharp.sln
dotnet build Graphify.CSharp.sln --configuration Release
dotnet test Graphify.CSharp.sln --configuration Release
dotnet pack src/Graphify.CSharp.Cli --configuration Release
```

See also [Compatibility](COMPATIBILITY.md), [Incremental indexing](INCREMENTAL_INDEXING.md),
and [Release and NuGet publishing](RELEASING.md).

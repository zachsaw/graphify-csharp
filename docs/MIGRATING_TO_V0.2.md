# Migrating to v0.2

v0.2 separates the command that keeps a Roslyn session warm from the commands
that ask it a question or publish JSON. Existing v0.1 watchers should be
stopped with the matching tool and started again with the v0.2 `watch` command;
each new watcher has a new session ID.

| v0.1 style | v0.2 replacement |
| --- | --- |
| `graphify-csharp ... --watch` | `graphify-csharp watch ...` |
| `graphify-csharp ... --watch --output out.json` | Start `watch`, find its ID with `ps`, then run `export --instance <id> --output out.json` |
| Re-run the ordinary extraction command to use a warm watcher | `graphify-csharp export --instance <id>` |
| Update a warm index without publishing JSON | `graphify-csharp refresh --instance <id>` |
| Force a warm rebuild | `graphify-csharp refresh --instance <id> --rebuild` |
| `graphify-out/graph.json` default | `graphify-out/csharp.json` default |

The bare command remains a convenient independent disk export:

```bash
graphify-csharp
graphify-csharp export
```

It can discover one solution/project in the immediate current root. A live
session is never selected from input, root, configuration, or output values;
use its full ID or a prefix that identifies exactly one session. `watch` never
writes JSON. `export` is the explicit publication request.

Relative input, root, output, and query-filter paths are resolved against the
calling process's current directory. `--root` controls analysis scope and
input discovery; it does not rebase the other paths. Use absolute paths or
change directory when preserving the old location semantics matters.

Queries and exports already wait for the selected session's startup or
recovery barrier. `refresh` is useful when an agent wants an explicit evidence
boundary or needs `--rebuild`; it is not a mandatory step before every query or
export.

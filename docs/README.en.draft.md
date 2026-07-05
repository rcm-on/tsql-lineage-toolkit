# T-SQL Lineage Toolkit

Static analysis engine for legacy T-SQL: column-level lineage, transitive impact
analysis, and a change-diff gate for pull requests. It is built to answer one
question that neither the SQL Server catalog nor generic SQL parsers answer
reliably for real T-SQL codebases: **what does this change actually reach,
including the part of the code hidden inside dynamic SQL?**

The output is designed for two consumers: engineers who need to know blast
radius before touching a stored procedure, and LLM agents that need a small,
navigable, per-object artifact instead of one monolithic graph file.

This is not a multi-dialect tool. It parses one dialect deeply (T-SQL) instead
of many dialects shallowly.

## What it measures, and where the numbers come from

Every number below is reproducible from an artifact in this repository. Each
row states its corpus and its caveat — there is no blanket "100% recall"
claim anywhere in this project, because none is true for every corpus.

| Metric | Value | Corpus | Caveat |
|---|---|---|---|
| Dependency-pair recall vs. SQL Server DMV oracle | 152/152 | AdventureWorks2019 (mostly static T-SQL) | Static-SQL corpus; does not generalize to dynamic-SQL-heavy code (see FRK row below) |
| View column-lineage recall vs. DMVs | 14/14, 12/12, 6/6 | WideWorldImporters (3 view samples) | One known gap: `PIVOT` unresolved in 1 AdventureWorks2019 view |
| Parse success | 11/11 files, 44,390 lines, 0 parse errors | First Responder Kit (FRK, Brent Ozar) | Production T-SQL with heavy dynamic SQL, `PIVOT`, `CROSS APPLY`, `OPENJSON`, table variables |
| End-to-end processing time | ~1.9 s total (`from-sql` 0.126 s + graph build 1.742 s) | Same FRK corpus (44,390 lines) | Single-machine timing via PowerShell `Measure-Command`; no internal parser-only instrumentation |
| Graph size on FRK | 5,691 nodes / 16,869 relationships | Same FRK corpus | Post alias-fix figures; the audit report's original run measured 5,710/16,928 before 16 defective edges were removed |
| Dependency pairs found beyond the native SQL Server catalog | 197 genuine (202 at audit time) | FRK vs. `sys.sql_expression_dependencies` | Of these, 61 come from reconstructing `EXEC(@sql)` / `sp_executesql` text, verified line-by-line against source; the rest are static objects (mostly DMVs/cross-db) the native catalog also fails to resolve. 5 of the original 202 were an alias-resolution defect, found by this audit and since fixed (0 alias artifacts remain in the graph) |
| Native SQL Server dependency catalog completeness on FRK | 1 resolvable dependency out of 111 declared (0.9%) | FRK, `sys.sql_expression_dependencies` | This is a property of the corpus (dynamic SQL, DMVs, cross-db refs), not a toolkit artifact — it is *why* "100% recall" against this oracle is not informative |
| xUnit test suite | 131 tests, in-process, CI on GitHub Actions | Toolkit's own test suite | .NET only; no Node.js in the critical gate path |

The FRK numbers are the dynamic-SQL story. The AdventureWorks2019/WWI numbers
are the static-SQL story. They are not interchangeable, and this README does
not merge them into a single headline figure.

## Quickstart

Build once:

```bash
dotnet build src/TSqlParser/TSqlParser.csproj -c Release
```

The pipeline has three stages: parse SQL files into an intermediate JSON,
build the dependency graph (plus an agent-friendly NodeStore), and — for a
pull request — diff two NodeStores.

Demo corpus — four one-statement files in `sql_before/`:

```sql
-- 01_tables.sql
CREATE TABLE dbo.Orders (Id INT PRIMARY KEY, Amount MONEY, CustomerId INT);
-- 02_view.sql
CREATE VIEW dbo.vOrderSummary AS SELECT CustomerId, SUM(Amount) AS Total FROM dbo.Orders GROUP BY CustomerId;
-- 03_proc.sql
CREATE PROCEDURE dbo.GetOrderSummary AS BEGIN SELECT CustomerId, Total FROM dbo.vOrderSummary; END
-- 04_consumer.sql
CREATE PROCEDURE dbo.ReportConsumer AS BEGIN SELECT * FROM dbo.OrderReport; END
```

### 1. Parse `.sql` files

```
$ dotnet TSqlParser.dll from-sql Demo before/input.json sql_before
  + sql_before/01_tables.sql -> Demo::dbo.Orders
  + sql_before/02_view.sql -> Demo::dbo.vOrderSummary
  + sql_before/03_proc.sql -> Demo::dbo.GetOrderSummary
  + sql_before/04_consumer.sql -> Demo::dbo.ReportConsumer
Wrote 4 objects from 4 file(s) to before/input.json
```

### 2. Build the graph and the NodeStore

```
$ dotnet TSqlParser.dll before/input.json before/graph.json --columns --nodestore
NodeStore: 3 objects, 12 shared nodes, 33 edges -> before/graph.nodes
Analyzed 3 objects (3 ok, 0 parse errors)
Analyzed 1 table schemas (1 ok, 0 errors)
Graph: 18 nodes, 33 relationships -> before/graph.json
```

`before/graph.nodes` is a directory (the NodeStore): one small JSON file per
object plus a manifest, an index, and a precomputed `change_map.json`.

### 3. Make a change and diff the impact

The procedure `dbo.GetOrderSummary` is edited to also write into
`dbo.OrderReport`, which `dbo.ReportConsumer` already reads:

```sql
-- 03_proc.sql (after)
CREATE PROCEDURE dbo.GetOrderSummary AS BEGIN
  SELECT CustomerId, Total FROM dbo.vOrderSummary;
  INSERT INTO dbo.OrderReport (CustomerId, Total) SELECT CustomerId, Total FROM dbo.vOrderSummary;
END
```

Re-running steps 1–2 against the edited files produces `after/graph.nodes`, whose
`change_map.json` now shows the new write:

```json
{
  "impact": {
    "Demo::dbo.GetOrderSummary": {
      "via_calls": [],
      "via_data": [
        { "table": "dbo.OrderReport", "consumers": ["dbo.ReportConsumer"] }
      ]
    }
  }
}
```

## The PR gate: `diff-change-map`

```
$ dotnet TSqlParser.dll diff-change-map before/graph.nodes after/graph.nodes diff.json --fail-on-new-impact
```

`diff-change-map` reads only `manifest.json` and `change_map.json` from each
store — it does not re-parse SQL, so it is cheap to run in CI. With
`--fail-on-new-impact`, the process exits non-zero when the change reaches a
consumer that did not depend on the touched code before.

Exit code from the run above: **2**. Real output (`diff.json`):

```json
{
  "objects_changed": ["Demo::dbo.GetOrderSummary"],
  "objects_added": [],
  "objects_removed": [],
  "impact_delta": {
    "Demo::dbo.GetOrderSummary": {
      "via_calls_added": [],
      "via_calls_removed": [],
      "via_data_added": [
        { "table": "dbo.OrderReport", "consumers": ["dbo.ReportConsumer"] }
      ],
      "via_data_removed": [],
      "newly_affected": ["dbo.ReportConsumer"]
    }
  },
  "workflows_delta": { "added": [], "removed": [], "reshaped": [] },
  "summary": {
    "changed": 1,
    "newly_affected_total": 1,
    "risk_note": "nuevo impacto alcanza: dbo"
  }
}
```

Without `--fail-on-new-impact` the same diff is written and the process
exits 0 — the flag only changes the exit code, not the analysis. A no-op
change (no content hash differs) produces an empty diff and exit 0
regardless of the flag.

This is the artifact a PR-comment bot or an MCP tool reads: `objects_changed`
for "what moved", `newly_affected` for "who is now exposed that wasn't
before".

## NodeStore: agent-first output

A monolithic `graph.json` for a large T-SQL codebase is not something an LLM
agent can load into context. The NodeStore trades that for a directory: one
small file per object (`objects/<Db>_<schema.name>/object.json` +
`nav.json`), a shared-nodes pool for tables/columns/actions referenced by
more than one object, an `index.json`, a `manifest.json` keyed by
`content_hash` per object, and a precomputed `change_map.json`. An agent (or
`diff-change-map`) reads a handful of small files instead of one large graph.
This is the most differentiated piece of the project — nothing directly
equivalent exists in the tools this toolkit was compared against (Microsoft
Purview, Redgate, SQLFlow/Datafold, SSDT/DACPAC, dbt Cloud).

## Honest limits

- **`EXEC @variable` is invisible to static analysis.** When the procedure
  name being invoked is a runtime-computed variable rather than a literal,
  `CALLS` cannot be produced — this is a ceiling of static analysis, not a
  bug. Measured on FRK: the corpus contains exactly one static literal
  proc-to-proc call (captured); every other proc-to-proc invocation in the
  kit routes through a variable.
- **`PIVOT` is unresolved in 1 AdventureWorks2019 view.** Documented, not
  hidden, in the view-lineage oracle comparison.
- **Single dialect by design.** The toolkit does not attempt multi-dialect
  coverage; it depends on T-SQL-specific constructs (dynamic SQL
  reconstruction, `MERGE`, recursive CTEs, temp-table bridging) that a
  generic parser handles only partially.
- **Cross-database and DMV references share a blind spot with SQL Server's
  own catalog.** `sys.sql_expression_dependencies` does not reliably track
  cross-db references or `sys.*` DMVs either — on FRK it resolves 1 of 111
  declared dependencies. The toolkit resolves more of these than the native
  catalog does, but neither has an authoritative oracle for this class of
  reference.
- (Fixed) The audit itself surfaced an alias-resolution defect —
  `UPDATE <alias> ... FROM <table> AS <alias>` recorded the alias as a table
  name (5 pairs, 16 edges, 0.09% of the FRK graph). It is fixed and
  regression-tested; it stays listed here as evidence that the audit
  numbers include the tool's own faults, not just its wins.

## How it's built

The engine is developed through orchestrated multi-model sessions rather than
a single continuous author: a frontier model handles design and audit against
closed specs, implementation is delegated against those specs, and the xUnit
suite acts as the regression net for every change. Audits include blind
mutation testing and independent re-counts against the oracles above before
a commit lands — the FRK numbers in this README, for instance, were
recomputed independently and corrected once after the first pass.

## License

MIT. See `LICENSE` (Copyright (c) 2024–2026 Ramón Campos Martín).

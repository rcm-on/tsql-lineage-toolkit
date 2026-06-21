# TSql Lineage Toolkit

**Deterministic data-lineage and impact-analysis engine for Microsoft SQL Server (T-SQL).**

Point it at your stored procedures — from a live SQL Server or from `.sql` files — and it builds a complete, queryable map of *what reads what, what writes where, and what breaks if you change it*. Down to the column. Through dynamic SQL. Without a running database at query time.

> Built for **Microsoft SQL Server / T-SQL** (parsed with the official `ScriptDom` grammar). Not a generic SQL tool — it understands T-SQL procedural code: cursors, dynamic `EXEC(@sql)`, `MERGE`, temporal tables, multi-level nesting.

---

## Why it exists

Before you rename a column, drop a table, or refactor a 2,000-line procedure, you need one answer you can trust: **what depends on this?**

The usual ways to get that answer don't hold up:

- **`sys.sql_expression_dependencies`** is famously incomplete — it misses dynamic SQL, deferred name resolution, and cross-database references.
- **Grep / text search** can't tell a real `IF` from one inside a `@sql` string being built, and can't follow a column through an `INSERT ... SELECT`.
- **An LLM reading the source** is non-deterministic: ask twice, get two different answers — disqualifying for a migration you have to sign off on.

This toolkit gives a **deterministic, grammar-aware** answer instead, as a portable artifact you can diff, version, and gate CI on.

---

## What makes it solid

| Virtue | What it means |
|---|---|
| **Deterministic** | Same input → same output, every run. Diffable, auditable, reproducible — not a chat answer. |
| **Grammar-aware** | Parses the real T-SQL AST. An `IF`/`EXEC` inside a dynamic-SQL *string* is not counted as control flow — where a text tool is fooled, the parser is right. |
| **Complete** | Resolves lineage through **dynamic SQL** (`EXEC(@sql)` built at runtime), **cursors**, **`INSERT INTO #temp SELECT`**, and **column-to-column derivation** — the cases other tools silently drop. |
| **Column-level** | "If I change `Sales.OrderLines.UnitPrice`, what breaks?" → traces the derived columns (`InvoiceLines.LineProfit`, `TaxAmount`, …) and every procedure on the chain. |
| **Queryable with SQL** | Ships a single SQLite database. Any impact / audit / aggregation question is one query — transitive impact via a recursive CTE, answered in milliseconds. |
| **Offline & portable** | Flat files. No server, no infrastructure. Analyze code that isn't even deployed yet. |

### Validated against real SQL

On WideWorldImporters (Microsoft's sample DB), the model was checked against the actual procedure source:

- `DeactivateTemporalTablesBeforeDataLoad`: model reports **34 dynamic `EXEC`** → the source has exactly **34**. Nesting reported as **1** → correct (each `EXEC` sits in a single `IF NOT EXISTS`), while a naïve grep reported **3**, fooled by `BEGIN`/`IF` tokens *inside* the generated SQL strings.

That gap — right where text analysis goes wrong — is the whole point.

---

## Quick start (Microsoft SQL Server)

```bash
cd src/TSqlParser

# A) From a live SQL Server (includes table DDL → column lineage + FKs)
dotnet run -- extract MyDatabase ../../input.json --server .\SQLEXPRESS --tables

# B) Or fully offline, from .sql files (one CREATE PROC/TABLE per file)
dotnet run -- from-sql MyDatabase ../../input.json sql/*.sql

# Build the lineage graph + the queryable SQLite database
dotnet run -- ../../input.json ../../graph_full.json --columns --sqlite --project=MyProject
```

Outputs:

- **`graph_full.json`** — the canonical, diffable lineage graph (version-control this).
- **`graph_full.db`** — a SQLite database you query with SQL (see below). Self-identifying: a `meta` table records the source **database**, **project**, and generation time.

---

## Querying impact and audits with SQL

The SQLite database has two tables — `nodes` and `edges` — plus a `meta` table. Common audit dimensions are promoted to indexed columns; full detail stays queryable as JSON (`json_extract`).

**"What breaks if I change a column?"** — transitive, through column derivation *and* call chains:

```sql
WITH RECURSIVE
  affected(col) AS (                                  -- the column + everything derived from it
    SELECT 'WideWorldImporters:table:sales.orderlines:column:UnitPrice'
    UNION SELECT e.src FROM edges e JOIN affected ON e.dst=affected.col
    WHERE e.type='DERIVES_FROM'),
  proc(p) AS (                                        -- procedures touching any affected column
    SELECT DISTINCT substr(e.src,1,instr(e.src,'#')-1)
    FROM edges e JOIN affected ON e.dst=affected.col
    WHERE e.type IN ('READS_COLUMN','WRITES_COLUMN')),
  impact(p,depth) AS (                                -- + whoever calls them (transitive)
    SELECT p,0 FROM proc
    UNION SELECT substr(e.src,1,instr(e.src,'#')-1), impact.depth+1
    FROM edges e JOIN impact ON e.dst=impact.p WHERE e.type='CALLS' AND impact.depth<10)
SELECT n.name, MIN(impact.depth) AS hops FROM impact JOIN nodes n ON n.id=impact.p
WHERE n.label='SqlObject' GROUP BY n.id ORDER BY hops, n.name;
```

**Audit & code-quality sweeps**, as one-line indexed queries:

```sql
-- procedures with no error handling (no TRY/CATCH)
SELECT name FROM nodes WHERE label='SqlObject' AND has_error_handling=0;

-- security: where is dynamic SQL built, and how much?
SELECT name, dynamic_sql_steps FROM nodes
WHERE label='SqlObject' AND dynamic_sql_steps>0 ORDER BY dynamic_sql_steps DESC;

-- destructive operations
SELECT action, COUNT(*) FROM nodes
WHERE label='Step' AND action IN ('DELETE','TRUNCATE','DROP') GROUP BY action;

-- schema governance: nullable / non-PK columns by data type
SELECT data_type, COUNT(*), SUM(is_nullable) AS nullable
FROM nodes WHERE label='Column' GROUP BY data_type;
```

Ready-made queries live in `scripts/lineage-queries.sql`; run one by tag with `node scripts/run-query.js @audit_dynamic_sql`. Or open `graph_full.db` in **DB Browser for SQLite**, **DBeaver**, or a VS Code SQLite extension.

---

## Where it fits

- **Pre-migration impact analysis** — know the blast radius before you touch a hot table or column.
- **CI deployment gate** — generate the graph on every PR; diff it to catch lineage that changed, or fail on new undocumented writes.
- **Security & code audits** — dynamic-SQL surface, missing error handling, destructive operations, cursor usage — all as queries.
- **LLM decision support** — a trustworthy fact base an agent queries (with SQL) instead of guessing over raw T-SQL.

---

## Limitations

Honest about what it does **not** do — so you know where to verify by hand:

- **Microsoft SQL Server / T-SQL only.** It parses the T-SQL grammar (`ScriptDom`); other dialects (PostgreSQL, MySQL, Oracle PL/SQL…) are out of scope.
- **Stores analyzed lineage, not source.** The literal `CREATE PROCEDURE` body is not in the graph or `.db` (it stays in `input.json`). Ask "what depends on X", not "show me the T-SQL of X".
- **Dynamic SQL is resolved only as far as it's statically reconstructable.** When `@sql` is built from values the parser can't see at analysis time, the step is flagged (`is_dynamic_sql = true`, target `(dynamic SQL)`) rather than resolved to a concrete table. Effects are inferred where the constructed string can be analyzed; otherwise surfaced as dynamic, not guessed.
- **No confidence scoring yet.** A statically-certain `READS_FROM` and a heuristically-inferred one currently look the same — you can't yet filter "certain vs inferred" edges. (Planned.)
- **Completeness is high but not total.** Lineage that flows only through constructs not yet walked can still be missed; on the reference corpus a handful of procedures still show no object-level edges. Each release narrows this, but treat absence of an edge as "not detected", not "proven none".
- **`cyclomatic_complexity` is the parser's own metric.** By construction it can't be cross-checked with a text tool; trust it as far as you trust the AST (independently validated here on dynamic-`EXEC` counts and nesting depth).
- **Parse errors skip the object.** A procedure `ScriptDom` cannot parse is reported and excluded, not partially analyzed.
- **Execution-plan enrichment is optional and separate.** Static analysis is the default; actual row counts / runtime-discovered tables require feeding in a plan XML.

## What it is not

- Not a generic multi-dialect SQL parser — it targets **Microsoft SQL Server / T-SQL**.
- Not a substitute for the source — it stores the *analyzed lineage*, not the literal `CREATE PROCEDURE` text.
- Not a runtime profiler — it reasons about code statically; pair it with execution-plan enrichment for real row counts.

---

*Targets Microsoft SQL Server (T-SQL), parsed with `Microsoft.SqlServer.TransactSql.ScriptDom`. Reference corpus: WideWorldImporters.*

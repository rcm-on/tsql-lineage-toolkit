# AI Agents & SQL Lineage: context cost, navigation method and proven gains

> Written by Ramón Campos Martín · [blog.rcmon.dev](https://blog.rcmon.dev)

---

## The problem: graph data is expensive for language models

When an AI agent needs to answer a lineage question — "what breaks if I rename `dbo.Customers.Email`?" — the naive approach is to load the whole graph and let the model reason over it.

That works fine for toy examples. For a real database it breaks down fast.

**WideWorldImporters reference corpus** (47 stored procedures, real Microsoft sample DB):

| File | Size | Estimated tokens* |
|---|---|---|
| `graph_full.json` | 1 510 KB | ~377 000 |
| `graph_full.nodes/` (full NodeStore) | 910 KB | ~227 000 |
| **NodeStore targeted query (3 files)** | **93 KB** | **~23 000** |

*≈ 4 chars / token (GPT-4 / Claude tokenizer average for JSON)

Claude Sonnet 4.6 has a 200 K-token context window. Loading `graph_full.json` alone **exceeds it** for mid-size databases. Even where it fits, paying for 377 000 input tokens per query is expensive and slow.

The token gap is not a constant — it widens with database size:

| DB size | graph_full.json | NodeStore query | Ratio |
|---|---|---|---|
| 10 objects | ~30 KB | ~8 KB | 3.7× |
| 47 objects (WideWorldImporters) | 1 510 KB | 93 KB | **16.2×** |
| 200 objects (typical enterprise SP) | ~6 MB | ~100–150 KB | **40–60×** |

---

## Why alternative approaches also fail

### Option A — Full graph load

```
Read graph_full.json (1.51 MB)
→ paste into context
→ ask model to filter 3 365 relationships manually
```

- Exceeds context at medium scale
- Model must do the graph traversal itself → hallucination risk on multi-hop queries
- No pre-computed impact chains (`AFFECTS via hop-2`)

### Option B — RAG / vector search over SQL source text

```
Embed each CREATE PROCEDURE text
→ semantic search for "Customers table"
→ return top-k chunks
```

- Returns SQL _text_, not structured lineage
- Cannot answer "which procs write to this table _indirectly_?"
- No execution plan data, no cyclomatic complexity, no condition paths

### Option C — Live Neo4j / graph DB

```
Agent → Cypher query → Neo4j → result
```

- Requires running infrastructure
- Not portable, not offline
- Still needs the agent to write correct multi-hop Cypher
- 47-object corpus: Cypher for indirect impact across 3 hops = 15-line query

### Option D — NodeStore (this toolkit)

```
Agent reads index.json + model.json + one targeted file
→ answer already structured with via/hops pre-computed
```

- Zero infrastructure (flat files on disk)
- Portable, offline, version-controllable
- Fits in 23 K tokens for most queries
- Pre-computed indirect impact chains — no graph traversal needed

---

## The NodeStore layout

```
graph_full.nodes/
  index.json        ~3 KB   schema, stats, navigation instructions ("howto")
  model.json        ~72 KB  all SqlObjects + Tables with aggregated edges
  manifest.json     ~8 KB   per-object content_hash + file paths

  objects/<db>::<schema>.<proc>/
    object.json              one proc: params, variables, steps, edges, condition_paths

  shared/
    tables/<id>.json         one table: all refs partitioned by contributing object
    columns/<id>.json        one column: reads/writes by object
    actions/<id>.json        one action rule
    rules/<id>.json          one condition rule
```

`index.json` tells an agent exactly what to read and why. It contains a `howto` block:

```json
"howto": {
  "impact_on_table":    ["index.json", "model.json", "shared/tables/<table>.json"],
  "what_does_proc_do":  ["index.json", "objects/<proc>/object.json"],
  "column_lineage":     ["index.json", "model.json", "shared/columns/<col>.json"],
  "condition_of_step":  ["index.json", "objects/<proc>/object.json → steps[n].condition_path"]
}
```

---

## Measured results: two real queries

### Query 1 — "What writes to `Warehouse.StockItems`, directly and indirectly?"

| Method | Files read | Bytes | Lookups | Time | Extra work for agent |
|---|---|---|---|---|---|
| Full graph load | 1 | 1 510 KB | filter 3 365 rels | 194 ms | Group by object, resolve indirect chains |
| **NodeStore** | **3** | **93 KB** | **3 directed reads** | **30 ms** | **None — refs already grouped with via/hops** |
| **Ratio** | — | **16.2×** | **1 120× fewer** | **6.5×** | — |

NodeStore answer (excerpt from `shared/tables/warehouse_stockitems.json`):

```json
"refs": [
  {
    "object": "WideWorldImporters::Warehouse.usp_StockItems_UpdateSellingPrice",
    "type": "WRITES_TO",
    "op": "UPDATE",
    "hops": 0,
    "via": null
  },
  {
    "object": "WideWorldImporters::Integration.GetStockItemUpdates",
    "type": "WRITES_TO",
    "op": "INSERT",
    "hops": 0,
    "via": null
  },
  {
    "object": "WideWorldImporters::DataLoadSimulation.Configuration_ApplyDataLoadSimulationProcedures",
    "type": "WRITES_TO",
    "op": "UPDATE",
    "hops": 1,
    "via": "Warehouse.usp_StockItems_UpdateSellingPrice"
  }
]
```

**Impact chain already resolved.** The agent reads three small files and the answer is ready. With full graph load, the agent must join 3 365 flat relationships, group by object, and walk `AFFECTS` chains — a multi-step reasoning task prone to errors.

---

### Query 2 — "Under what conditions does `step18` execute?"

`step18` is an `INSERT` nested 4 levels deep: `WHILE → IF_ELSE → IF → IF`.

| Method | Files read | Bytes | Hops to reconstruct chain | Time |
|---|---|---|---|---|
| Full graph load | 1 | 84 KB | **9** (GOVERNS + 4×NESTED_IN + invert order) | 92 ms |
| **NodeStore** | **1** | **73 KB** | **1** (read `step18.condition_path`) | **17 ms** |
| **Ratio** | — | 1.2× | **9×** | **5.4×** |

NodeStore delivers the chain already ordered (outer → inner), no graph traversal needed:

```json
"condition_path": [
  "WHILE: @@FETCH_STATUS = 0",
  "IF_ELSE: NOT (@AvailableQty IS NULL)",
  "IF: @AvailableQty < @RequiredQty",
  "IF: @ApprovalStatus = 'APPROVED'"
]
```

With full graph load, the agent must: find `GOVERNS` edge for `step18` → walk 4 `NESTED_IN` edges → collect Rule nodes in reverse order → invert the chain. Each hop is a reasoning step the model can get wrong.

---

## Cost analysis (API pricing, June 2026)

Using **Claude Sonnet 4.6** ($3 / M input tokens) and the WideWorldImporters corpus:

| Approach | Input tokens | Cost per query |
|---|---|---|
| Full graph load | 377 000 | **$1.13** |
| NodeStore (3 files) | 23 000 | **$0.07** |
| **Saving** | — | **16× cheaper** |

At 100 lineage queries/day (CI pipeline, developer tool, automated audit):

| | Daily cost | Monthly cost |
|---|---|---|
| Full graph load | $113 | $3 390 |
| **NodeStore** | **$7** | **$210** |

This gap multiplies with database size. For a 200-object enterprise database, the ratio reaches **40–60×** — NodeStore queries stay near the same token count while the full graph grows linearly.

---

## Agent navigation protocol

This is the recommended reading order for any LLM-based agent:

```
1. Read index.json
   → understand: node types, edge types, total counts, howto map
   → decide: which howto pattern matches the user question

2. Read model.json
   → scan: all SqlObject entries (full_name, cyclomatic_complexity, writes_to_agg)
   →       all Table entries (name, degree, path to shared file)
   → locate: the object or table of interest by name

3. Read ONE targeted file (from model.json's path field)
   → objects/<proc>/object.json   for procedure questions
   → shared/tables/<id>.json      for table impact questions
   → shared/columns/<id>.json     for column lineage questions

4. Follow edges selectively (only where needed)
   → each edge in object.json carries "path" pointing to the neighbour file
   → read only the neighbours you need — not the whole graph
```

**Total reads: 3 files for most questions. Maximum: index + model + object + 2 neighbours = 5 files.**

### Example: Claude Code agent session

```
User: What procs write to dbo.Clientes, directly or via a call chain?

Agent:
  Tool: Read("graph_full.nodes/index.json")
  → finds howto.impact_on_table pattern, locates model.json path

  Tool: Read("graph_full.nodes/model.json")
  → finds Table entry for "dbo.Clientes", path = "shared/tables/dwh_pro__dbo.clientes_3f8a1c.json"

  Tool: Read("graph_full.nodes/shared/tables/dwh_pro__dbo.clientes_3f8a1c.json")
  → refs: spCargarClientes (INSERT, hops=0), spActualizarSegmentos (UPDATE, hops=0)

  Answer: 2 procedures write to dbo.Clientes directly. No indirect chains detected.
  Total tokens used: ~18 400. Cost: $0.06.
```

### Integration patterns

| Framework | How to use NodeStore |
|---|---|
| **Claude Code** | `Read` tool on individual `.json` files — native, zero setup |
| **GitHub Copilot Chat** | `#file:` references to `index.json` + targeted files |
| **LangChain / LlamaIndex** | `JSONLoader` per file, tool per howto pattern |
| **OpenAI function calling** | `get_table_impact(table)`, `get_proc_detail(proc)` → each reads 1–2 files |
| **AutoGen / CrewAI** | Agent tool: `read_nodestore_file(path)` + `list_objects()` from model.json |
| **Custom RAG** | Index `model.json` entries as documents; retrieve → targeted file read |

---

## Comparison summary

| Dimension | Full JSON load | RAG / vector | Live graph DB | **NodeStore** |
|---|---|---|---|---|
| Token cost | 16–60× baseline | Varies | N/A (API) | **Baseline** |
| Context fit (200-obj DB) | Fails | Partial | N/A | **Always fits** |
| Multi-hop chains | Model must reason | No | Cypher query | **Pre-computed** |
| Condition paths | Reconstruct (9 hops) | No | Cypher (complex) | **Direct field** |
| Infrastructure | None | Vector DB | Graph DB | **None** |
| Offline / portable | Yes | No | No | **Yes** |
| Incremental updates | Regenerate all | Re-embed | Schema migration | **`update-nodestore`** |
| Version control | 1 large file | Index + chunks | External | **Many small files** |
| Execution plan data | In JSON | No | Possible | **In edge properties** |

---

## Generating the NodeStore

```bash
cd src/TSqlParser

# From a live SQL Server (recommended: includes table DDL)
dotnet run -- extract MyDatabase ../../input.json --server .\SQLEXPRESS --tables
dotnet run -- ../../input.json ../../graph_full.json --columns --nodestore

# From .sql files (offline)
dotnet run -- from-sql MyDatabase ../../input.json sql/*.sql
dotnet run -- ../../input.json ../../graph_full.json --columns --nodestore

# Incremental update (only rewrites changed objects)
dotnet run -- update-nodestore ../../input.json ../../graph_full.nodes --columns
# → "Updated: 1 objects (46 unchanged), shared: 3 (740 unchanged)"
```

The NodeStore and `graph_full.json` are generated in the same pass. **No extra cost** at generation time.

---

## CI pipeline integration

Run the lineage analysis and NodeStore generation as part of your deployment gate:

```yaml
# .github/workflows/lineage.yml
- name: Extract SQL definitions
  run: dotnet run -- extract $DB input.json --server $SQL_SERVER --tables

- name: Generate lineage graph + NodeStore
  run: dotnet run -- input.json graph.json --columns --nodestore

- name: Check for new undocumented tables
  run: |
    # Fail if execution plan discovers tables not in static analysis
    dotnet run -- enrich-from-plans graph.json graph_enriched.json $PLAN_FILE
    # parse graph_enriched.json, count discovered=true edges → fail if > threshold
```

---

## Conclusion

The NodeStore is not a workaround — it is the correct data structure for LLM-based lineage queries. It trades storage (a partitioned directory vs one big file) for three things that matter for agents:

1. **Token efficiency** — 16–60× fewer tokens per query, fits any context window
2. **Pre-computed answers** — impact chains, condition paths, indirect hops already resolved
3. **Directed navigation** — every edge carries a `path` field pointing to the next file to read

The result: an agent answering "what breaks if I change this table?" reads 3 files in 30 ms at 93 KB, instead of filtering 3 365 relationships across 1.5 MB of JSON. At production query volumes, this is the difference between a useful tool and an unusable one.

---

*Source data: WideWorldImporters (Microsoft sample database), TSql Lineage Toolkit v1, June 2026.*  
*Generated NodeStore: 47 objects, 1 384 nodes, 3 365 relationships, 743 shared nodes.*  
*Timing measured on a mid-range laptop (Ryzen 5 5600, NVMe SSD), cold file-system cache.*

**→ [Back to README](../README.md) · [Generate your NodeStore](#generating-the-nodestore)**

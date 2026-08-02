-- Saved lineage queries for graph.db (built by build-sqlite.js).
-- Schema:
--   nodes(id, label, name, cyclomatic_complexity, total_steps,
--         dynamic_sql_steps, unresolved_dynamic_sql_steps, max_nesting, db)
--   edges(src, dst, type, props)   -- props = JSON of the edge properties
--
-- Each query below answers one of the questions we validated this session.
-- Run a single one with:  node scripts/run-query.js "<query>"   (or the named tag)
--
-- CONFIDENCE BUCKETS (@impact, @col_impact). column/table-reference edges
-- (READS_COLUMN/WRITES_COLUMN/FILTERS_ON, and READS_FROM/WRITES_TO) carry a
-- `resolution` property in `props` telling you HOW the engine knows about that
-- reference: "direct" (written literally in the SQL), "star_expanded" (came from
-- expanding a SELECT */alias.*), or via a `via_view` property (reached by resolving
-- past a VIEW down to its base table/column). These are not equally certain, and
-- pretending they are is exactly the bug this session's queries used to have (a flat
-- list looks the same whether an edge is deduced or literal). Bucketed as:
--   Seguro    - resolution=direct.
--   Probable  - star_expanded or via_view. NOT lower-quality evidence, just a
--               different convention: via_view especially is the ENGINE resolving
--               further than most oracles do (they stop at the view), not a defect
--               - measured 98.0% precision for star_expanded on 1,523 edges, 99.8%
--               for direct on 3,870 (see ColumnRecallGateTests.MinPrecisionByClass /
--               notes/task-confianza-al-consumidor.md). These numbers are the actual
--               confidence - never invent a number, and never multiply confidences
--               along a chain (errors are correlated, not independent) - take MIN().
--   No lo sé  - objects in the same database with unresolved_dynamic_sql_steps > 0:
--               EXEC of dynamically-built SQL that never resolved to a literal, so
--               the engine has NO edge for what they touch (fails closed rather than
--               guessing) and can't be filtered to "objects that touch this table" -
--               if we knew the table, the SQL would have resolved. Listed as an
--               unconditional disclaimer, not "no impact found".

-- @ranking  Top procedures by complexity (+ steps, dynamic SQL, nesting, fan-out).
SELECT n.name, n.cyclomatic_complexity AS cc, n.total_steps AS steps,
       n.dynamic_sql_steps AS dyn, n.max_nesting AS nest,
       (SELECT COUNT(*) FROM edges e WHERE e.type='CALLS' AND e.src=n.id) AS calls_out
FROM nodes n
WHERE n.label='SqlObject'
ORDER BY n.cyclomatic_complexity DESC, n.max_nesting DESC
LIMIT 5;

-- @impact  Transitive impact of changing a table, bucketed by CONFIDENCE instead of
-- a flat "everyone who touches it" list (see the CONFIDENCE BUCKETS note above).
-- Seguro = direct READS_FROM/WRITES_TO (hop 1, no via_view). Probable = reached
-- through a view (hop 1, via_view) OR only reached transitively through a CALLS
-- chain (hop > 1 - a real dependency, but a weaker one: the calling proc might not
-- always execute that branch). No lo sé = same-database objects whose dynamic SQL
-- never resolved, so they might touch this table and the engine can't tell you
-- either way. READS_FROM/WRITES_TO originate at Step nodes ("<objId>#step<N>"), so
-- we roll the step up to its owning SqlObject (substr before '#') - the same rollup
-- model.json precomputed; here it's just an expression, computed at query time.
-- Replace the seed id with your table. Find it with:
--   SELECT id FROM nodes WHERE label='Table' AND name='<TableName>';
WITH RECURSIVE seed(t) AS (VALUES ('WideWorldImporters:table:application.cities')),
direct_hits(o, hops, bucket, reason) AS (
  SELECT
    CASE WHEN instr(e.src,'#')>0 THEN substr(e.src,1,instr(e.src,'#')-1) ELSE e.src END,
    1,
    CASE WHEN json_extract(e.props,'$.via_view') IS NOT NULL THEN 'Probable' ELSE 'Seguro' END,
    CASE WHEN json_extract(e.props,'$.via_view') IS NOT NULL THEN 'via vista' ELSE NULL END
  FROM edges e, seed
  WHERE e.dst = seed.t AND e.type IN ('READS_FROM','WRITES_TO')
  UNION
  SELECT CASE WHEN instr(e.src,'#')>0 THEN substr(e.src,1,instr(e.src,'#')-1) ELSE e.src END,
         w.hops + 1, 'Probable', 'llamada indirecta'
  FROM edges e JOIN direct_hits w ON e.dst = w.o
  WHERE e.type = 'CALLS' AND w.hops < 10
),
best_per_object AS (
  -- an object reached BOTH directly and transitively keeps its best (Seguro) rank -
  -- min() over evidence, never averaged/multiplied with the weaker path.
  SELECT o, MIN(CASE bucket WHEN 'Seguro' THEN 1 ELSE 2 END) AS rank, MIN(hops) AS min_hops
  FROM direct_hits GROUP BY o
),
bucketed AS (
  SELECT n.name AS obj_name,
         CASE WHEN b.rank = 1 THEN 'Seguro' ELSE 'Probable' END AS bucket,
         CASE WHEN b.rank = 1 THEN NULL
              WHEN b.min_hops = 1 THEN 'via vista'
              ELSE 'llamada indirecta' END AS reason
  FROM best_per_object b JOIN nodes n ON n.id = b.o WHERE n.label='SqlObject'
),
blind AS (
  SELECT n.name AS obj_name, 'No lo sé' AS bucket, 'SQL dinámico sin resolver' AS reason
  FROM nodes n, seed
  WHERE n.label='SqlObject' AND n.unresolved_dynamic_sql_steps > 0
    AND n.db = substr(seed.t, 1, instr(seed.t, ':') - 1)
),
reason_counts AS (
  SELECT bucket, reason, COUNT(*) AS n
  FROM (SELECT * FROM bucketed UNION ALL SELECT * FROM blind)
  GROUP BY bucket, reason
)
SELECT bucket, SUM(n) AS objects,
       GROUP_CONCAT(CASE WHEN reason IS NOT NULL THEN n || ' ' || reason END, ', ') AS detail
FROM reason_counts GROUP BY bucket
ORDER BY CASE bucket WHEN 'Seguro' THEN 1 WHEN 'Probable' THEN 2 ELSE 3 END;

-- @col_impact  Column-level impact, bucketed by CONFIDENCE instead of a flat list
-- (see the CONFIDENCE BUCKETS note above). For the column you're about to
-- change/drop: which objects reference it directly (Seguro), which only through
-- SELECT */alias.* expansion or through a view (Probable, broken down by reason),
-- and which same-database objects run dynamic SQL that never resolved and so might
-- reference it without any edge to prove or disprove it (No lo sé). Reads
-- READS_COLUMN/WRITES_COLUMN/FILTERS_ON (same edge set ColumnRecallGateTests
-- measures precision against), rolling each Step up to its owning SqlObject.
-- Replace the seed id with your column. Find it with:
--   SELECT id FROM nodes WHERE label='Column' AND name='<ColName>';
-- (the id encodes db:table:column, so it disambiguates same-named columns.)
WITH seed(col) AS (VALUES ('WideWorldImporters:table:sales.orderlines:column:UnitPrice')),
col_edges AS (
  SELECT
    CASE WHEN instr(e.src,'#')>0 THEN substr(e.src,1,instr(e.src,'#')-1) ELSE e.src END AS obj,
    json_extract(e.props, '$.resolution') AS resolution
  FROM edges e, seed
  WHERE e.dst = seed.col AND e.type IN ('READS_COLUMN','WRITES_COLUMN','FILTERS_ON')
),
per_object AS (
  -- min() over evidence per object: ANY direct reference is enough certainty,
  -- regardless of weaker star_expanded/via_view edges the same object might also
  -- have to this column - never average/multiply them down.
  SELECT obj,
         MAX(resolution = 'direct') AS has_direct,
         MAX(resolution = 'via_view') AS has_via_view,
         MAX(resolution = 'star_expanded') AS has_star
  FROM col_edges GROUP BY obj
),
bucketed AS (
  SELECT n.name AS obj_name,
         CASE WHEN p.has_direct THEN 'Seguro'
              WHEN p.has_via_view OR p.has_star THEN 'Probable' END AS bucket,
         CASE WHEN p.has_direct THEN NULL
              WHEN p.has_via_view THEN 'via vista'
              WHEN p.has_star THEN 'de SELECT *' END AS reason
  FROM per_object p JOIN nodes n ON n.id = p.obj WHERE n.label='SqlObject'
),
blind AS (
  SELECT n.name AS obj_name, 'No lo sé' AS bucket, 'SQL dinámico sin resolver' AS reason
  FROM nodes n, seed
  WHERE n.label='SqlObject' AND n.unresolved_dynamic_sql_steps > 0
    AND n.db = substr(seed.col, 1, instr(seed.col, ':') - 1)
),
reason_counts AS (
  SELECT bucket, reason, COUNT(*) AS n
  FROM (SELECT * FROM bucketed UNION ALL SELECT * FROM blind)
  GROUP BY bucket, reason
)
SELECT bucket, SUM(n) AS objects,
       GROUP_CONCAT(CASE WHEN reason IS NOT NULL THEN n || ' ' || reason END, ', ') AS detail
FROM reason_counts GROUP BY bucket
ORDER BY CASE bucket WHEN 'Seguro' THEN 1 WHEN 'Probable' THEN 2 ELSE 3 END;

-- @col_derives_chain  (formerly the flat @col_impact) Column-level DATA-VALUE
-- lineage, WITH DEPTH + ordered chain: not "who references this column" but "whose
-- VALUE is computed from this one", i.e. DERIVES_FROM (INSERT...SELECT / computed
-- columns), a genuinely different question from @col_impact above and kept under
-- its own tag rather than folded into the confidence buckets - DERIVES_FROM isn't
-- classified by resolution (it's excluded from ColumnRecallGateTests' ColumnRefEdges
-- on purpose: it doesn't map to "referenced this column" in the oracle's sense).
-- DERIVES_FROM points consumer -> source, so downstream impact walks edges
-- backwards (dst = a column we already reached, src = a new consumer of it). The
-- idpath column carries the visited node ids for an O(1) cycle guard (instr), so a
-- computed column feeding itself transitively can't loop forever.
-- Replace the seed id with your column. Find it with:
--   SELECT id FROM nodes WHERE label='Column' AND name='<ColName>';
-- (the id encodes db:table:column, so it disambiguates same-named columns.)
WITH RECURSIVE impact(col, depth, idpath, chain) AS (
  SELECT e.src, 1,
         e.dst || '|' || e.src,
         substr(e.dst, instr(e.dst, ':table:') + 7) || ' -> ' || substr(e.src, instr(e.src, ':table:') + 7)
  FROM edges e
  WHERE e.dst = 'WideWorldImporters:table:sales.orderlines:column:UnitPrice'
    AND e.type = 'DERIVES_FROM'
  UNION ALL
  SELECT e.src, i.depth + 1,
         i.idpath || '|' || e.src,
         i.chain || ' -> ' || substr(e.src, instr(e.src, ':table:') + 7)
  FROM edges e JOIN impact i ON e.dst = i.col
  WHERE e.type = 'DERIVES_FROM' AND i.depth < 20
    AND instr(i.idpath, e.src) = 0)
SELECT substr(col, instr(col, ':table:') + 7) AS affected_column, hops, chain
FROM (SELECT col, depth AS hops, chain,
             ROW_NUMBER() OVER (PARTITION BY col ORDER BY depth) AS rn
      FROM impact)
WHERE rn = 1 ORDER BY hops, affected_column;

-- @col_provenance  The mirror of @col_impact: where a column's value COMES FROM,
-- with depth + ordered chain. Walks DERIVES_FROM forwards (src = the column we
-- reached, dst = a source it derives from) to the ultimate origin column(s) - the
-- "ordered remediation" view: fix the deepest source first, then work outward.
WITH RECURSIVE prov(col, depth, idpath, chain) AS (
  SELECT e.dst, 1,
         e.src || '|' || e.dst,
         substr(e.src, instr(e.src, ':table:') + 7) || ' <- ' || substr(e.dst, instr(e.dst, ':table:') + 7)
  FROM edges e
  WHERE e.src = 'WideWorldImporters:table:sales.orderlines:column:LineTotal'
    AND e.type = 'DERIVES_FROM'
  UNION ALL
  SELECT e.dst, p.depth + 1,
         p.idpath || '|' || e.dst,
         p.chain || ' <- ' || substr(e.dst, instr(e.dst, ':table:') + 7)
  FROM edges e JOIN prov p ON e.src = p.col
  WHERE e.type = 'DERIVES_FROM' AND p.depth < 20
    AND instr(p.idpath, e.dst) = 0)
SELECT substr(col, instr(col, ':table:') + 7) AS source_column, hops, chain
FROM (SELECT col, depth AS hops, chain,
             ROW_NUMBER() OVER (PARTITION BY col ORDER BY depth) AS rn
      FROM prov)
WHERE rn = 1 ORDER BY hops, source_column;

-- @coupling  SP-to-SP coupling: fan-out (calls made) and fan-in (called by).
SELECT n.name,
       (SELECT COUNT(*) FROM edges e WHERE e.type='CALLS' AND e.src=n.id) AS fan_out,
       (SELECT COUNT(*) FROM edges e WHERE e.type='CALLS' AND e.dst=n.id) AS fan_in
FROM nodes n WHERE n.label='SqlObject'
  AND (EXISTS (SELECT 1 FROM edges e WHERE e.type='CALLS' AND (e.src=n.id OR e.dst=n.id)))
ORDER BY fan_out DESC, fan_in DESC;

-- @criticality  How many distinct tables each proc writes to (blast radius).
-- NOTE: READS_FROM/WRITES_TO edges start at Step nodes ("<objId>#stepN"), so any
-- per-OBJECT aggregation must roll the step up to its owner (substr before '#').
SELECT n.name, COUNT(DISTINCT e.dst) AS tables_written, n.cyclomatic_complexity AS cc
FROM edges e
JOIN nodes n ON n.id = CASE WHEN instr(e.src,'#')>0
                            THEN substr(e.src,1,instr(e.src,'#')-1) ELSE e.src END
WHERE e.type='WRITES_TO' AND n.label='SqlObject'
GROUP BY n.id ORDER BY tables_written DESC LIMIT 10;

-- @workflows  Interpret the workflows: write footprint per workflow.
SELECT n.name AS workflow, COUNT(DISTINCT e.dst) AS tables_written
FROM nodes n JOIN edges e ON e.src = n.id AND e.type='WORKFLOW_WRITES_TO'
WHERE n.label='Workflow'
GROUP BY n.id ORDER BY tables_written DESC;

-- @hottables  Most-touched tables (read+write degree) — the schema hubs.
SELECT n.name, COUNT(*) AS degree
FROM nodes n JOIN edges e ON e.dst = n.id AND e.type IN ('READS_FROM','WRITES_TO')
WHERE n.label='Table'
GROUP BY n.id ORDER BY degree DESC LIMIT 10;

-- ── Audit queries (use the promoted columns — indexed, no JSON scan) ──────────

-- @audit_no_error_handling  Robustness: procedures with no TRY/CATCH.
SELECT name FROM nodes WHERE label='SqlObject' AND has_error_handling=0 ORDER BY name;

-- @audit_dynamic_sql  Security: procedures that build/run dynamic SQL, ranked by how much.
SELECT name, dynamic_sql_steps, total_steps FROM nodes
WHERE label='SqlObject' AND dynamic_sql_steps > 0 ORDER BY dynamic_sql_steps DESC;

-- @audit_destructive  Risk: steps performing destructive operations.
SELECT action, COUNT(*) AS n FROM nodes
WHERE label='Step' AND action IN ('DELETE','TRUNCATE','DROP')
GROUP BY action ORDER BY n DESC;

-- @audit_cursors  Performance: procedures using cursors.
SELECT name FROM nodes WHERE label='SqlObject' AND has_cursor=1 ORDER BY name;

-- @inventory  What kinds of objects exist, by type and schema.
SELECT object_type, schema_name, COUNT(*) AS n FROM nodes
WHERE label='SqlObject' GROUP BY object_type, schema_name ORDER BY n DESC;

-- @schema_governance  Nullable / non-PK columns by data type (data-quality sweep).
SELECT data_type, COUNT(*) AS cols,
       SUM(is_nullable) AS nullable, SUM(is_primary_key) AS pks
FROM nodes WHERE label='Column' AND data_type IS NOT NULL
GROUP BY data_type ORDER BY cols DESC LIMIT 12;

-- @provenance  Which database/project this .db was generated from, and when.
SELECT key, value FROM meta ORDER BY key;

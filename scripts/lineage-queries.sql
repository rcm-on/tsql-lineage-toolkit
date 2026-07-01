-- Saved lineage queries for graph.db (built by build-sqlite.js).
-- Schema:
--   nodes(id, label, name, cyclomatic_complexity, total_steps,
--         dynamic_sql_steps, max_nesting, db)
--   edges(src, dst, type, props)   -- props = JSON of the edge properties
--
-- Each query below answers one of the questions we validated this session.
-- Run a single one with:  node scripts/run-query.js "<query>"   (or the named tag)

-- @ranking  Top procedures by complexity (+ steps, dynamic SQL, nesting, fan-out).
SELECT n.name, n.cyclomatic_complexity AS cc, n.total_steps AS steps,
       n.dynamic_sql_steps AS dyn, n.max_nesting AS nest,
       (SELECT COUNT(*) FROM edges e WHERE e.type='CALLS' AND e.src=n.id) AS calls_out
FROM nodes n
WHERE n.label='SqlObject'
ORDER BY n.cyclomatic_complexity DESC, n.max_nesting DESC
LIMIT 5;

-- @impact  Transitive impact of changing a table: every object that reads/writes
-- it directly, plus everything that CALLS those (up to 10 hops). The killer query
-- a recursive CTE answers in one shot — no JSON scan, no graph traversal by hand.
-- READS_FROM/WRITES_TO originate at Step nodes ("<objId>#step<N>"), so we roll the
-- step up to its owning SqlObject (substr before '#') — the same rollup model.json
-- precomputed; here it's just an expression, computed at query time.
WITH RECURSIVE owner(o, depth) AS (
  SELECT DISTINCT
    CASE WHEN instr(src,'#')>0 THEN substr(src,1,instr(src,'#')-1) ELSE src END, 1
  FROM edges
  WHERE dst = 'WideWorldImporters:table:application.cities'
    AND type IN ('READS_FROM','WRITES_TO')
  UNION
  SELECT CASE WHEN instr(e.src,'#')>0 THEN substr(e.src,1,instr(e.src,'#')-1) ELSE e.src END,
         w.depth+1
  FROM edges e JOIN owner w ON e.dst = w.o
  WHERE e.type='CALLS' AND w.depth < 10)
SELECT n.name, MIN(w.depth) AS hops
FROM owner w JOIN nodes n ON n.id = w.o
WHERE n.label='SqlObject'
GROUP BY n.id ORDER BY hops, n.name;

-- @col_impact  Column-level transitive impact, WITH DEPTH + ordered chain. For the
-- column you're about to change/drop, lists every column whose value derives from
-- it, how many DERIVES_FROM hops away (depth), and the exact origin -> ... ->
-- consumer path. DERIVES_FROM points consumer -> source, so downstream impact walks
-- edges backwards (dst = a column we already reached, src = a new consumer of it).
-- The idpath column carries the visited node ids for an O(1) cycle guard (instr),
-- so a computed column feeding itself transitively can't loop forever.
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

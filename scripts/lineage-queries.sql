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

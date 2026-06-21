// Builds a queryable SQLite database from graph_full.json.
//
// This is the "query surface" export: the same deterministic facts the parser
// produces (nodes/edges, with per-object scalars rolled up), in a single .db an
// agent/LLM can query with one SQL statement instead of scanning JSON files.
//
//   node scripts/build-sqlite.js [graph_full.json] [graph.db]
//
// Requires Node 22.5+ (built-in node:sqlite). No npm dependency.
const fs = require('node:fs');
const { DatabaseSync } = require('node:sqlite');

const inPath = process.argv[2] || 'out/graph_full.json';
const outPath = process.argv[3] || 'out/graph.db';

const g = JSON.parse(fs.readFileSync(inPath, 'utf8').replace(/^﻿/, ''));

// Roll up step-level facts to the owning SqlObject (a Step id is "<objId>#step<N>").
const steps = { total: {}, dynamic: {}, maxNest: {} };
for (const n of g.nodes) {
  if (!n.labels?.includes('Step')) continue;
  const owner = n.id.split('#')[0];
  steps.total[owner] = (steps.total[owner] || 0) + 1;
  if (n.properties?.is_dynamic_sql === true) steps.dynamic[owner] = (steps.dynamic[owner] || 0) + 1;
  steps.maxNest[owner] = Math.max(steps.maxNest[owner] || 0, n.properties?.nesting_level ?? 0);
}

fs.rmSync(outPath, { force: true });
const db = new DatabaseSync(outPath);
db.exec(`
  CREATE TABLE nodes(
    id TEXT PRIMARY KEY, label TEXT, name TEXT,
    cyclomatic_complexity INTEGER, total_steps INTEGER,
    dynamic_sql_steps INTEGER, max_nesting INTEGER, db TEXT);
  CREATE TABLE edges(src TEXT, dst TEXT, type TEXT, props TEXT);
`);

const insN = db.prepare(`INSERT OR IGNORE INTO nodes VALUES (?,?,?,?,?,?,?,?)`);
const insE = db.prepare(`INSERT INTO edges VALUES (?,?,?,?)`);

db.exec('BEGIN');
for (const n of g.nodes) {
  const label = n.labels?.[0] || 'Node';
  const p = n.properties || {};
  const name = p.full_name || p.name || n.id;
  insN.run(
    n.id, label, name,
    p.cyclomatic_complexity ?? null,
    label === 'SqlObject' ? (steps.total[n.id] ?? 0) : null,
    label === 'SqlObject' ? (steps.dynamic[n.id] ?? 0) : null,
    label === 'SqlObject' ? (steps.maxNest[n.id] ?? 0) : null,
    p.database ?? null);
}
for (const r of g.relationships) {
  insE.run(r.source, r.target, r.type, r.properties ? JSON.stringify(r.properties) : null);
}
db.exec('COMMIT');

db.exec(`
  CREATE INDEX ix_edges_src ON edges(src);
  CREATE INDEX ix_edges_dst ON edges(dst);
  CREATE INDEX ix_edges_type ON edges(type);
  CREATE INDEX ix_nodes_label ON nodes(label);
`);

const n = db.prepare('SELECT COUNT(*) c FROM nodes').get().c;
const e = db.prepare('SELECT COUNT(*) c FROM edges').get().c;
db.close();
console.log(`graph.db -> ${outPath}  (${n} nodes, ${e} edges)`);
